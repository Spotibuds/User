using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Hubs;
namespace User.Services;

// Recipient commands and snapshots share one clock, owner boundary and delivery contract.
public sealed class NotificationCommands(MongoDbContext db, IHubContext<NotificationHub> hub, TimeProvider clock, ILogger<NotificationCommands>? logger = null)
{
    private readonly MongoTransactions transactions = new(db.Database.Client);
    private DateTime Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()).UtcDateTime;
    private static string Account(string user) => SocialCommands.Account(user);
    private static string Id(string id) { Input.ObjectId(id); return MongoDB.Bson.ObjectId.Parse(id).ToString(); }
    public FilterDefinition<Notification> Active(string user, DateTime? now = null) => Builders<Notification>.Filter.Where(n => n.TargetUserId == user && n.DismissedAt == null && (n.ExpiresAt == null || n.ExpiresAt > (now ?? Now)));
    private static FilterDefinition<Notification> Through(Notification anchor, bool inclusive) => Builders<Notification>.Filter.Lt(n => n.CreatedAt, anchor.CreatedAt) | (Builders<Notification>.Filter.Eq(n => n.CreatedAt, anchor.CreatedAt) & (inclusive ? Builders<Notification>.Filter.Lte(n => n.Id, anchor.Id) : Builders<Notification>.Filter.Lt(n => n.Id, anchor.Id)));
    private async Task<List<NotificationDto>> Project(List<Notification> rows, CancellationToken ct = default, IClientSessionHandle? session = null)
    {
        var ids = rows.Where(n => n.Type == NotificationType.Follow && n.SourceUserId != null).Select(n => n.SourceUserId!).Distinct().ToList();
        var publicActors = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Count > 0)
        {
            var query = session == null ? db.Users.Find(x => ids.Contains(x.IdentityUserId) && !x.IsPrivate) : db.Users.Find(session, x => ids.Contains(x.IdentityUserId) && !x.IsPrivate);
            publicActors = (await query.Project(x => x.IdentityUserId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        }
        return rows.Select(n => NotificationDto.From(n, n.Type == NotificationType.Follow ? $"/user/{(n.SourceUserId != null && publicActors.Contains(n.SourceUserId) ? n.SourceUserId : n.TargetUserId)}" : null)).ToList();
    }
    private sealed record Observation(NotificationSnapshot Snapshot, NotificationDto? Selected);
    public async Task<NotificationSnapshot> Snapshot(string user, int limit = 50, int skip = 0, string? before = null) => (await Observe(user, limit, skip, before)).Snapshot;
    private async Task<Observation> Observe(string user, int limit, int skip = 0, string? before = null, string? selectedId = null)
    {
        user = Account(user); Input.Page(limit, skip);
        if (before != null && skip != 0) throw new ApiProblem(400, "Use either a cursor or an offset.");
        if (before != null) before = Id(before);
        var now = Now;
        return await transactions.Run(async (session, ct) =>
        {
            var page = Builders<Notification>.Filter.Empty;
            if (before != null)
            {
                var anchor = await db.Notifications.Find(session, n => n.Id == before && n.TargetUserId == user).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Notification cursor not found.");
                page = Through(anchor, inclusive: false);
            }
            var sort = Builders<Notification>.Sort.Descending(n => n.CreatedAt).Descending(n => n.Id);
            var facets = await db.Notifications.Aggregate(session).Match(Active(user, now)).Facet(
                AggregateFacet.Create("items", new EmptyPipelineDefinition<Notification>().Match(page).Sort(sort).Skip(skip).Limit(limit + 1)),
                AggregateFacet.Create("total", new EmptyPipelineDefinition<Notification>().Count()),
                AggregateFacet.Create("unread", new EmptyPipelineDefinition<Notification>().Match(n => n.Status == NotificationStatus.Unread).Count()),
                AggregateFacet.Create("selected", new EmptyPipelineDefinition<Notification>().Match(n => n.Id == selectedId).Limit(1))).SingleAsync(ct);
            var rows = facets.Facets.Single(f => f.Name == "items").Output<Notification>().ToList();
            var more = rows.Count > limit; rows = rows.Take(limit).ToList();
            var total = facets.Facets.Single(f => f.Name == "total").Output<AggregateCountResult>().FirstOrDefault()?.Count ?? 0;
            var unread = facets.Facets.Single(f => f.Name == "unread").Output<AggregateCountResult>().FirstOrDefault()?.Count ?? 0;
            var selected = facets.Facets.Single(f => f.Name == "selected").Output<Notification>().ToList();
            var projected = await Project(rows.Concat(selected).ToList(), ct, session);
            var snapshot = new NotificationSnapshot(projected.Take(rows.Count).ToList(), total, (int)Math.Min(int.MaxValue, unread), limit, skip, more ? rows[^1].Id : null);
            return new Observation(snapshot, selected.Count == 0 ? null : projected[^1]);
        });
    }
    public async Task<int> Unread(string user) => (int)Math.Min(int.MaxValue, await db.Notifications.CountDocumentsAsync(Active(Account(user)) & Builders<Notification>.Filter.Eq(n => n.Status, NotificationStatus.Unread)));
    private async Task Deliver(Func<CancellationToken, Task> operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await operation(timeout.Token); }
        catch (Exception ex) { logger?.LogWarning("Committed notification state delivery failed ({ErrorType}). Recover through recipient snapshot.", ex.GetType().Name); }
    }
    public async Task Changed(string user)
    {
        user = Account(user);
        // Invalidation is delivered even when a subsequent count dependency is unavailable.
        await Deliver(ct => hub.Clients.Group($"notifications_{user}").SendAsync("NotificationsChanged", new { userId = user }, ct));
        await Deliver(async ct => await hub.Clients.Group($"notifications_{user}").SendAsync("UnreadCountUpdate", await Unread(user).WaitAsync(ct), ct));
    }
    public async Task Publish(Notification n)
    {
        // A delayed producer must never publish a dismissed or superseded unread snapshot.
        await Deliver(async ct =>
        {
            var current = await db.Notifications.Find(Active(n.TargetUserId) & Builders<Notification>.Filter.Eq(x => x.Id, n.Id)).FirstOrDefaultAsync(ct);
            if (current != null) await hub.Clients.Group($"notifications_{current.TargetUserId}").SendAsync("NewNotification", (await Project([current], ct)).Single(), ct);
        });
        await Changed(n.TargetUserId);
    }
    private async Task<NotificationAcknowledgement> Acknowledge(string message, string user, Notification? n = null, string? throughId = null)
    {
        // The write is already committed. Readback failure must not turn it into a false failed write.
        try
        {
            var observed = await Observe(user, 1, selectedId: n?.Id);
            return new(message, observed.Selected, observed.Snapshot.TotalCount, observed.Snapshot.UnreadCount, throughId);
        }
        catch (Exception ex) when (ex is ApiProblem || DependencyErrors.IsDependency(ex))
        {
            logger?.LogWarning("Committed notification acknowledgement requires snapshot retry ({ErrorType}).", ex.GetType().Name);
            return new(message, ThroughId: throughId, SynchronizationPending: true);
        }
    }
    public async Task<NotificationAcknowledgement> Read(string id, string user, bool handled = false)
    {
        id = Id(id); user = Account(user); var now = Now;
        var result = await transactions.Run(async (session, ct) =>
        {
            var n = await db.Notifications.Find(session, Active(user, now) & Builders<Notification>.Filter.Eq(x => x.Id, id)).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Notification not found.");
            var changed = handled ? n.Status != NotificationStatus.Handled : n.Status == NotificationStatus.Unread;
            if (changed)
            {
                if (handled) { n.Status = NotificationStatus.Handled; n.HandledAt = now; }
                else { n.Status = NotificationStatus.Read; n.ReadAt = now; }
                n.UpdatedAt = now;
                await db.Notifications.ReplaceOneAsync(session, x => x.Id == id && x.TargetUserId == user, n, cancellationToken: ct);
            }
            return (n, changed);
        });
        if (result.changed)
        {
            await Deliver(ct => hub.Clients.Group($"notifications_{user}").SendAsync(handled ? "NotificationHandled" : "NotificationMarkedRead", result.n.Id, ct));
            await Changed(user);
        }
        return await Acknowledge(handled ? "Notification handled" : "Notification read", user, result.n);
    }
    private async Task<Notification?> Boundary(string user, string? id)
    {
        if (id != null) return await db.Notifications.Find(n => n.Id == Id(id) && n.TargetUserId == user).FirstOrDefaultAsync() ?? throw new ApiProblem(404, "Notification boundary not found.");
        return await db.Notifications.Find(Active(user)).SortByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).FirstOrDefaultAsync();
    }
    public async Task<NotificationAcknowledgement> ReadAll(string user, string? throughId = null)
    {
        user = Account(user); var boundary = await Boundary(user, throughId); var now = Now;
        if (boundary != null)
        {
            var filter = Active(user, now) & Through(boundary, true) & Builders<Notification>.Filter.Eq(n => n.Status, NotificationStatus.Unread);
            var result = await db.Notifications.UpdateManyAsync(filter, Builders<Notification>.Update.Set(n => n.Status, NotificationStatus.Read).Set(n => n.ReadAt, now).Set(n => n.UpdatedAt, now));
            if (result.ModifiedCount > 0)
            {
                await Deliver(ct => hub.Clients.Group($"notifications_{user}").SendAsync("AllNotificationsMarkedRead", ct));
                await Changed(user);
            }
        }
        return await Acknowledge("Notifications read", user, throughId: boundary?.Id);
    }
    public async Task<NotificationAcknowledgement> Dismiss(string id, string user)
    {
        id = Id(id); user = Account(user);
        var result = await db.Notifications.UpdateOneAsync(n => n.Id == id && n.TargetUserId == user && n.DismissedAt == null, Builders<Notification>.Update.Set(n => n.DismissedAt, Now).Set(n => n.UpdatedAt, Now));
        if (result.MatchedCount == 0 && !await db.Notifications.Find(n => n.Id == id && n.TargetUserId == user).AnyAsync()) throw new ApiProblem(404, "Notification not found.");
        if (result.ModifiedCount > 0)
        {
            await Deliver(ct => hub.Clients.Group($"notifications_{user}").SendAsync("NotificationDeleted", new { id, userId = user }, ct));
            await Changed(user);
        }
        return await Acknowledge("Notification deleted", user);
    }
    public async Task<NotificationAcknowledgement> DismissAll(string user, string? throughId = null)
    {
        user = Account(user); var boundary = await Boundary(user, throughId); var now = Now;
        if (boundary != null)
        {
            var result = await db.Notifications.UpdateManyAsync(Active(user, now) & Through(boundary, true), Builders<Notification>.Update.Set(n => n.DismissedAt, now).Set(n => n.UpdatedAt, now));
            if (result.ModifiedCount > 0) await Changed(user);
        }
        return await Acknowledge("Notifications deleted", user, throughId: boundary?.Id);
    }
    public async Task<NotificationAcknowledgement> Cleanup(string user, int days = 30)
    {
        user = Account(user); if (days is < 1 or > 365) throw new ApiProblem(400, "Invalid retention.");
        var cutoff = Now.AddDays(-days);
        var result = await db.Notifications.DeleteManyAsync(n => n.TargetUserId == user && (n.Status == NotificationStatus.Handled && n.HandledAt < cutoff || n.DismissedAt < cutoff));
        if (result.DeletedCount > 0) await Changed(user);
        return await Acknowledge("Notifications cleaned", user);
    }
}
