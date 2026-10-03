using MongoDB.Driver;
using Microsoft.AspNetCore.SignalR;
using User.Data;
using User.Entities;
using User.Hubs;
namespace User.Services;
public interface INotificationService
{
    Task<Notification> CreateNotificationAsync(string targetUserId, NotificationType type, string title, string message, string? sourceUserId = null, Dictionary<string, object>? data = null, string? actionUrl = null, string? key = null);
    Task<List<Notification>> GetUserNotificationsAsync(string userId, int limit = 50, int skip = 0);
    Task<int> GetUnreadCountAsync(string userId);
    Task MarkAsReadAsync(string notificationId, string userId);
    Task MarkAsHandledAsync(string notificationId, string userId);
    Task MarkAllAsReadAsync(string userId);
    Task CleanupOldNotificationsAsync(string userId, int daysOld = 30);
    Task<Notification> PersistAsync(IClientSessionHandle session, Notification notification, CancellationToken ct);
    Task PublishAsync(Notification notification);
    Task RefreshCountAsync(string userId);
}
public class NotificationService(MongoDbContext db, IHubContext<NotificationHub> hub, TimeProvider clock) : INotificationService
{
    public async Task<Notification> PersistAsync(IClientSessionHandle session, Notification n, CancellationToken ct)
    {
        if (n.Key == null) { await db.Notifications.InsertOneAsync(session, n, cancellationToken: ct); return n; }
        var existing = await db.Notifications.Find(session, x => x.Key == n.Key).FirstOrDefaultAsync(ct);
        if (existing != null) return existing;
        await db.Notifications.InsertOneAsync(session, n, cancellationToken: ct); return n;
    }
    public async Task PublishAsync(Notification n)
    {
        await hub.Clients.Group($"notifications_{n.TargetUserId}").SendAsync("NewNotification", Dto(n));
        await Count(n.TargetUserId);
    }
    public Task RefreshCountAsync(string userId) => Count(userId);
    public static object Dto(Notification n) => new { n.Id, type = n.Type.ToString(), status = n.Status.ToString(), n.Title, n.Message, n.TargetUserId, n.SourceUserId, n.CreatedAt, n.Data, n.ActionUrl, n.ReadAt, n.HandledAt, n.ExpiresAt };
    private FilterDefinition<Notification> Active(string user) => Builders<Notification>.Filter.Where(n => n.TargetUserId == user && (n.ExpiresAt == null || n.ExpiresAt > clock.GetUtcNow().UtcDateTime));
    public async Task<Notification> CreateNotificationAsync(string targetUserId, NotificationType type, string title, string message, string? sourceUserId = null, Dictionary<string, object>? data = null, string? actionUrl = null, string? key = null)
    {
        if (actionUrl != null && (!actionUrl.StartsWith('/') || actionUrl.StartsWith("//"))) throw new ApiProblem(400, "Invalid notification link.");
        var n = new Notification { TargetUserId = targetUserId, SourceUserId = sourceUserId, Type = type, Title = title, Message = message, Data = data ?? new(), ActionUrl = actionUrl, CreatedAt = clock.GetUtcNow().UtcDateTime, ExpiresAt = type == NotificationType.FriendRequest ? clock.GetUtcNow().UtcDateTime.AddDays(30) : null };
        n.Key = key;
        if (key == null) await db.Notifications.InsertOneAsync(n);
        else n = await db.Notifications.FindOneAndUpdateAsync(x => x.Key == key, Builders<Notification>.Update.SetOnInsert(x => x.Id, n.Id).SetOnInsert(x => x.Key, key).SetOnInsert(x => x.TargetUserId, n.TargetUserId).SetOnInsert(x => x.SourceUserId, n.SourceUserId).SetOnInsert(x => x.Type, n.Type).SetOnInsert(x => x.Title, n.Title).SetOnInsert(x => x.Message, n.Message).SetOnInsert(x => x.Data, n.Data).SetOnInsert(x => x.ActionUrl, n.ActionUrl).SetOnInsert(x => x.Status, n.Status).SetOnInsert(x => x.CreatedAt, n.CreatedAt).SetOnInsert(x => x.ExpiresAt, n.ExpiresAt), new FindOneAndUpdateOptions<Notification> { IsUpsert = true, ReturnDocument = ReturnDocument.After });
        await PublishAsync(n);
        return n;
    }
    public async Task<List<Notification>> GetUserNotificationsAsync(string userId, int limit = 50, int skip = 0) { Input.Page(limit, skip); return await db.Notifications.Find(Active(userId)).SortByDescending(n => n.CreatedAt).Skip(skip).Limit(limit).ToListAsync(); }
    public async Task<int> GetUnreadCountAsync(string userId) => (int)await db.Notifications.CountDocumentsAsync(Active(userId) & Builders<Notification>.Filter.Eq(n => n.Status, NotificationStatus.Unread));
    private async Task Count(string userId) => await hub.Clients.Group($"notifications_{userId}").SendAsync("UnreadCountUpdate", await GetUnreadCountAsync(userId));
    public async Task MarkAsReadAsync(string id, string user)
    {
        Input.ObjectId(id);
        var result = await db.Notifications.UpdateOneAsync(n => n.Id == id && n.TargetUserId == user && n.Status == NotificationStatus.Unread, Builders<Notification>.Update.Set(n => n.Status, NotificationStatus.Read).Set(n => n.ReadAt, clock.GetUtcNow().UtcDateTime));
        if (result.MatchedCount == 0 && !await db.Notifications.Find(n => n.Id == id && n.TargetUserId == user).AnyAsync()) throw new ApiProblem(404, "Notification not found.");
        await Count(user);
    }
    public async Task MarkAsHandledAsync(string id, string user) { Input.ObjectId(id); var result = await db.Notifications.UpdateOneAsync(n => n.Id == id && n.TargetUserId == user, Builders<Notification>.Update.Set(n => n.Status, NotificationStatus.Handled).Set(n => n.HandledAt, clock.GetUtcNow().UtcDateTime)); if (result.MatchedCount == 0) throw new ApiProblem(404, "Notification not found."); await Count(user); }
    public async Task MarkAllAsReadAsync(string user) { await db.Notifications.UpdateManyAsync(Active(user) & Builders<Notification>.Filter.Eq(n => n.Status, NotificationStatus.Unread), Builders<Notification>.Update.Set(n => n.Status, NotificationStatus.Read).Set(n => n.ReadAt, clock.GetUtcNow().UtcDateTime)); await Count(user); }
    public async Task CleanupOldNotificationsAsync(string user, int daysOld = 30) { if (daysOld is < 1 or > 365) throw new ApiProblem(400, "Invalid retention."); await db.Notifications.DeleteManyAsync(n => n.TargetUserId == user && n.Status == NotificationStatus.Handled && n.HandledAt < clock.GetUtcNow().UtcDateTime.AddDays(-daysOld)); await Count(user); }
}
