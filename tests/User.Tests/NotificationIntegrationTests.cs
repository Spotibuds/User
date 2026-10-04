using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using User.Entities;
using User.Hubs;
using User.Services;
using Xunit;

namespace User.Tests;
public class NotificationIntegrationTests
{
    private static async Task<JsonObject> Object(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    private static async Task<Notification> Create(UserFactory f, string? target = null, string? key = null)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<INotificationService>().CreateNotificationAsync(target ?? f.Bob, NotificationType.Other, "Canonical alert", "Persisted content", f.Alice, actionUrl: "/friends", key: key);
    }
    private static Notification Row(UserFactory f, string? target = null, DateTime? at = null) => new() { TargetUserId = target ?? f.Bob, SourceUserId = f.Alice, Title = "Stored alert", Message = "Stored content", Type = NotificationType.Other, CreatedAt = at ?? f.Clock.Value.UtcDateTime };
    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 120 && !predicate(); i++) await Task.Delay(25);
        Assert.True(predicate(), "Expected notification event was not received.");
    }

    [Fact]
    public async Task CanonicalCreationMatchesRestAndRealtimePersistedIdTypeAndBsonTimestamp()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob);
        await using var hub = f.Hub("/notification-hub", f.Bob); var events = new ConcurrentQueue<JsonElement>(); hub.On<JsonElement>("NewNotification", events.Enqueue); await hub.StartAsync();
        f.Clock.Value = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero).AddTicks(4567);
        var created = await Create(f); await Until(() => events.Count == 1);
        var envelope = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}")); var dto = envelope["notifications"]!.AsArray().Single()!;
        Assert.Equal(created.Id, dto["id"]!.GetValue<string>()); Assert.Equal(created.Id, events.Single().GetProperty("id").GetString()); Assert.True(ObjectId.TryParse(created.Id, out _));
        Assert.Equal("Other", dto["type"]!.GetValue<string>()); Assert.Equal("Unread", dto["status"]!.GetValue<string>()); Assert.Equal(f.Bob, dto["targetUserId"]!.GetValue<string>()); Assert.Equal(f.Alice, dto["sourceUserId"]!.GetValue<string>());
        Assert.Equal(dto.ToJsonString(), JsonNode.Parse(events.Single().GetRawText())!.ToJsonString()); Assert.Equal(1, envelope["totalCount"]!.GetValue<int>()); Assert.Equal(1, envelope["unreadCount"]!.GetValue<int>());
        var raw = await f.Db.Database.GetCollection<BsonDocument>(f.Db.Notifications.CollectionNamespace.CollectionName).Find(new BsonDocument("_id", ObjectId.Parse(created.Id))).SingleAsync();
        var timestampFields = raw.Names.Where(x => x.Equals("createdAt", StringComparison.OrdinalIgnoreCase)).ToList(); Assert.Single(timestampFields);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(f.Clock.Value.ToUnixTimeMilliseconds()).UtcDateTime, raw[timestampFields.Single()].ToUniversalTime());
        Assert.Equal((await f.Db.Notifications.Find(x => x.Id == created.Id).SingleAsync()).CreatedAt, created.CreatedAt);
    }

    [Fact]
    public async Task LegacyTimestampMigrationPreservesAuthoritativeTimeAndRepairsCursorAndBulkFence()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob);
        f.Clock.Value = new DateTimeOffset(2031, 4, 6, 12, 0, 0, TimeSpan.Zero);
        var newest = Row(f); var middle = Row(f, at: f.Clock.Value.UtcDateTime.AddHours(-1)); var oldest = Row(f, at: f.Clock.Value.UtcDateTime.AddHours(-2)); var canonical = Row(f, at: f.Clock.Value.UtcDateTime.AddHours(-3));
        var raw = f.Db.Database.GetCollection<BsonDocument>(f.Db.Notifications.CollectionNamespace.CollectionName);
        var documents = new[] { newest, middle, oldest }.Select(n =>
        {
            var value = n.ToBsonDocument(); value["CreatedAt"] = new BsonDateTime(n.CreatedAt); value["createdAt"] = new BsonDateTime(f.Clock.Value.UtcDateTime.AddDays(1)); return value;
        }).ToList(); documents.Add(canonical.ToBsonDocument()); await raw.InsertManyAsync(documents);
        Assert.Equal(3, (await IndexInitializer.MigrateNotificationTimestamps(f.Db)).ModifiedCount); Assert.Equal(0, (await IndexInitializer.MigrateNotificationTimestamps(f.Db)).ModifiedCount);
        await Until(() => f.Services.GetRequiredService<IndexState>().Ready);
        await raw.Indexes.DropOneAsync("notification_timeline");
        await raw.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { ["TargetUserId"] = 1, ["CreatedAt"] = -1, ["_id"] = -1 }, new CreateIndexOptions { Name = "notification_timeline" }));
        await IndexInitializer.EnsureNotificationTimeline(f.Db); await IndexInitializer.EnsureNotificationTimeline(f.Db);
        using var indexes = await raw.Indexes.ListAsync();
        var timeline = Assert.Single(await indexes.ToListAsync(), value => value["name"] == "notification_timeline");
        Assert.Equal(new BsonDocument { ["TargetUserId"] = 1, ["createdAt"] = -1, ["_id"] = -1 }, timeline["key"].AsBsonDocument);
        foreach (var row in new[] { newest, middle, oldest, canonical })
        {
            var value = await raw.Find(new BsonDocument("_id", ObjectId.Parse(row.Id))).SingleAsync();
            Assert.Single(value.Names, x => x.Equals("createdAt", StringComparison.OrdinalIgnoreCase)); Assert.False(value.Contains("CreatedAt")); Assert.Equal(row.CreatedAt, value["createdAt"].ToUniversalTime()); Assert.Equal(row.CreatedAt, (await f.Db.Notifications.Find(n => n.Id == row.Id).SingleAsync()).CreatedAt);
        }
        var first = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}?limit=2")); Assert.Equal(new[] { newest.Id, middle.Id }, first["notifications"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        var cursor = first["nextBefore"]!.GetValue<string>(); Assert.Equal(middle.Id, cursor);
        var second = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}?limit=2&before={cursor}")); Assert.Equal(new[] { oldest.Id, canonical.Id }, second["notifications"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        var read = await Object(await bob.PostAsJsonAsync($"/api/notifications/{f.Bob}/read-all", new { throughId = middle.Id })); Assert.Equal(1, read["unreadCount"]!.GetValue<int>());
        foreach (var row in new[] { middle, oldest, canonical }) Assert.Equal(NotificationStatus.Read, (await f.Db.Notifications.Find(n => n.Id == row.Id).SingleAsync()).Status);
        Assert.Equal(NotificationStatus.Unread, (await f.Db.Notifications.Find(n => n.Id == newest.Id).SingleAsync()).Status);
    }

    [Fact]
    public async Task AnonymousCrossUserAndClientForgeAttemptsHaveNoSideEffects()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob); using var alice = f.Client(f.Alice); using var anonymous = f.Client(); var n = await Create(f);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/notifications/{f.Bob}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/notifications/{f.Bob}")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/notifications/{f.Bob}/unread-count")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync($"/api/notifications/{n.Id}/read", null)).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync($"/api/notifications/{n.Id}/handle", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/api/notifications/{n.Id}")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/notifications/{n.Id}?userId={f.Bob}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync($"/api/notifications/{f.Bob}/read-all", null)).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/notifications/{f.Bob}/all")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/notifications/{f.Bob}/cleanup")).StatusCode);
        await using var hub = f.Hub("/notification-hub", f.Alice); await hub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("SendNotificationToUser", f.Bob, new { title = "forged" })); await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("CreateNotification", f.Bob, "forged")); await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("MarkAsRead", n.Id)); await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("Dismiss", n.Id));
        var stored = await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync(); Assert.Equal(NotificationStatus.Unread, stored.Status); Assert.Null(stored.DismissedAt); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true));
        Assert.Equal(1, (await Object(await bob.GetAsync($"/api/notifications/{f.Bob.ToUpperInvariant()}")))["unreadCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExpirationAndDismissalUseOneClockForAllSnapshotCounts()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob); f.Clock.Value = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var active = Row(f); active.ExpiresAt = f.Clock.Value.UtcDateTime.AddHours(1); var expired = Row(f); expired.ExpiresAt = f.Clock.Value.UtcDateTime.AddSeconds(-1); var dismissed = Row(f); dismissed.DismissedAt = f.Clock.Value.UtcDateTime;
        await f.Db.Notifications.InsertManyAsync(new[] { active, expired, dismissed });
        var snapshot = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}")); Assert.Single(snapshot["notifications"]!.AsArray()); Assert.Equal(active.Id, snapshot["notifications"]!.AsArray().Single()!["id"]!.GetValue<string>()); Assert.Equal(1, snapshot["totalCount"]!.GetValue<int>()); Assert.Equal(1, snapshot["unreadCount"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/notifications/{expired.Id}/read", null)).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/notifications/{dismissed.Id}/handle", null)).StatusCode);
    }

    [Fact]
    public async Task EqualTimestampOrderingAndCursorStayStableAcrossInsertAndDismissedAnchor()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob);
        var rows = Enumerable.Range(0, 17).Select(_ => Row(f)).ToList(); await f.Db.Notifications.InsertManyAsync(rows); var expected = rows.OrderByDescending(x => x.Id, StringComparer.Ordinal).Select(x => x.Id).ToList();
        var first = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}?limit=5")); var firstIds = first["notifications"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToList(); Assert.Equal(expected.Take(5), firstIds); var cursor = first["nextBefore"]!.GetValue<string>();
        var newer = Row(f, at: f.Clock.Value.UtcDateTime.AddSeconds(1)); await f.Db.Notifications.InsertOneAsync(newer); Assert.Equal(HttpStatusCode.OK, (await bob.DeleteAsync($"/api/notifications/{cursor}")).StatusCode);
        var second = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}?limit=5&before={cursor}")); Assert.Equal(expected.Skip(5).Take(5), second["notifications"]!.AsArray().Select(x => x!["id"]!.GetValue<string>())); Assert.Equal(17, second["totalCount"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync($"/api/notifications/{f.Bob}?limit=101")).StatusCode); Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync($"/api/notifications/{f.Bob}?limit=0")).StatusCode); Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync($"/api/notifications/{f.Bob}?skip=-1")).StatusCode); Assert.Equal(HttpStatusCode.BadRequest, (await bob.GetAsync($"/api/notifications/{f.Bob}?skip=1&before={cursor}")).StatusCode);
        var foreign = Row(f, target: f.Mallory); await f.Db.Notifications.InsertOneAsync(foreign); Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/notifications/{f.Bob}?before={foreign.Id}")).StatusCode);
    }

    [Fact]
    public async Task ReadHandleAreIdempotentStableAndDoNotRevertTerminalState()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob); var n = await Create(f);
        var first = await Object(await bob.PostAsync($"/api/notifications/{n.Id}/read", null)); Assert.Equal("Read", first["notification"]!["status"]!.GetValue<string>()); Assert.Equal(0, first["unreadCount"]!.GetValue<int>());
        var readAt = (await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync()).ReadAt; f.Clock.Value = f.Clock.Value.AddHours(1);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => bob.PostAsync($"/api/notifications/{n.Id}/read", null))); Assert.Equal(readAt, (await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync()).ReadAt);
        await bob.PostAsync($"/api/notifications/{n.Id}/handle", null); var handledAt = (await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync()).HandledAt; f.Clock.Value = f.Clock.Value.AddHours(1);
        await bob.PostAsync($"/api/notifications/{n.Id}/handle", null); await bob.PostAsync($"/api/notifications/{n.Id}/read", null); var stored = await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync(); Assert.Equal(NotificationStatus.Handled, stored.Status); Assert.Equal(handledAt, stored.HandledAt); Assert.Equal(readAt, stored.ReadAt);
    }

    [Fact]
    public async Task PendingFriendRequestRemainsActionableAfterReadAll()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        var sent = await Object(await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Bob })); var id = sent["requestId"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/notifications/{f.Bob}/read-all", null)).StatusCode); var list = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}")); var notice = list["notifications"]!.AsArray().Single()!; Assert.Equal("Read", notice["status"]!.GetValue<string>()); Assert.Equal(id, notice["data"]!["requestId"]!.GetValue<string>()); Assert.Equal(0, list["unreadCount"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{id}/accept", null)).StatusCode); Assert.Equal(NotificationStatus.Handled, (await f.Db.Notifications.Find(n => n.TargetUserId == f.Bob).SingleAsync()).Status);
    }

    [Fact]
    public async Task ReadAllAndDismissAllFenceNewArrivalsAndRejectForeignBoundaries()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob); var old = await Create(f, key: "old"); f.Clock.Value = f.Clock.Value.AddSeconds(1); var newer = await Create(f, key: "new");
        var read = await Object(await bob.PostAsJsonAsync($"/api/notifications/{f.Bob}/read-all", new { throughId = old.Id })); Assert.Equal(old.Id, read["throughId"]!.GetValue<string>()); Assert.Equal(1, read["unreadCount"]!.GetValue<int>()); Assert.Equal(NotificationStatus.Unread, (await f.Db.Notifications.Find(x => x.Id == newer.Id).SingleAsync()).Status);
        var dismissed = await Object(await bob.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/notifications/{f.Bob}/all") { Content = JsonContent.Create(new { throughId = old.Id }) })); Assert.Equal(1, dismissed["totalCount"]!.GetValue<int>()); Assert.Equal(1, dismissed["unreadCount"]!.GetValue<int>());
        var foreign = await Create(f, target: f.Mallory); Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/notifications/{f.Bob}/read-all", new { throughId = foreign.Id })).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await bob.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/notifications/{f.Bob}/all") { Content = JsonContent.Create(new { throughId = foreign.Id }) })).StatusCode);
        Assert.Equal(NotificationStatus.Unread, (await f.Db.Notifications.Find(x => x.Id == newer.Id).SingleAsync()).Status);
    }

    [Fact]
    public async Task DismissalSynchronizesBothTabsAndKeepsKeyTombstone()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob); var n = await Create(f, key: "once");
        await using var one = f.Hub("/notification-hub", f.Bob); await using var two = f.Hub("/notification-hub", f.Bob); var countsOne = new ConcurrentQueue<int>(); var countsTwo = new ConcurrentQueue<int>(); var deletedOne = new ConcurrentQueue<JsonElement>(); var deletedTwo = new ConcurrentQueue<JsonElement>();
        one.On<int>("UnreadCountUpdate", countsOne.Enqueue); two.On<int>("UnreadCountUpdate", countsTwo.Enqueue); one.On<JsonElement>("NotificationDeleted", deletedOne.Enqueue); two.On<JsonElement>("NotificationDeleted", deletedTwo.Enqueue);
        await Task.WhenAll(one.StartAsync(), two.StartAsync()); Assert.Equal(HttpStatusCode.OK, (await bob.DeleteAsync($"/api/notifications/{n.Id}")).StatusCode); await Until(() => deletedOne.Count == 1 && deletedTwo.Count == 1 && countsOne.LastOrDefault(-1) == 0 && countsTwo.LastOrDefault(-1) == 0);
        Assert.Equal(n.Id, deletedOne.Single().GetProperty("id").GetString()); Assert.Equal(f.Bob, deletedTwo.Single().GetProperty("userId").GetString()); Assert.NotNull((await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync()).DismissedAt);
        Assert.Equal(n.Id, (await Create(f, key: "once")).Id); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true)); Assert.Equal(HttpStatusCode.OK, (await bob.DeleteAsync($"/api/notifications/{n.Id}")).StatusCode); Assert.Empty((await Object(await bob.GetAsync($"/api/notifications/{f.Bob}")))["notifications"]!.AsArray());
    }

    [Fact]
    public async Task ConcurrentLogicalEventCreationPersistsAndPublishesExactlyOnce()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); await using var hub = f.Hub("/notification-hub", f.Bob); var events = new ConcurrentQueue<JsonElement>(); hub.On<JsonElement>("NewNotification", events.Enqueue); await hub.StartAsync();
        var records = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Create(f, key: "logical-event"))); Assert.Single(records.Select(n => n.Id).Distinct()); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true)); await Until(() => events.Count == 1);
        var replay = await Create(f, key: "logical-event"); Assert.Equal(records[0].CreatedAt, replay.CreatedAt);
        using var scope = f.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<INotificationService>(); var conflict = await Assert.ThrowsAsync<ApiProblem>(() => service.CreateNotificationAsync(f.Mallory, NotificationType.Other, "Conflict", "Wrong recipient", f.Alice, key: "logical-event")); Assert.Equal(409, conflict.Status);
        await Task.Delay(150); Assert.Single(events);
    }

    [Fact]
    public async Task InvalidRecipientsActorsReferencesAndLinksCannotCreateOrphans()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var scope = f.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<INotificationService>();
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiProblem>(() => service.CreateNotificationAsync(Guid.NewGuid().ToString(), NotificationType.Other, "Missing", "Unknown", f.Alice))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiProblem>(() => service.CreateNotificationAsync(f.Bob, NotificationType.Other, "Missing", "Unknown", Guid.NewGuid().ToString()))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblem>(() => service.CreateNotificationAsync(f.Bob, NotificationType.Other, "Self", "Wrong", f.Bob))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblem>(() => service.CreateNotificationAsync(f.Bob, (NotificationType)99, "Wrong", "Wrong", f.Alice))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblem>(() => service.CreateNotificationAsync(f.Bob, NotificationType.Other, "Wrong", "Wrong", f.Alice, actionUrl: "//outside.invalid"))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblem>(() => service.CreateNotificationAsync(f.Bob, NotificationType.Message, "Missing message", "Wrong", f.Alice))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiProblem>(() => service.CreateNotificationAsync(f.Bob, NotificationType.Message, "Missing message", "Wrong", f.Alice, new() { ["chatId"] = ObjectId.GenerateNewId().ToString(), ["messageId"] = ObjectId.GenerateNewId().ToString() }, "/chat/missing"))).Status);
        Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task HubReconnectRecoversSnapshotAndDeliversOnceWithoutForeignLeakage()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); await using var bob = f.Hub("/notification-hub", f.Bob); await using var alice = f.Hub("/notification-hub", f.Alice); var events = new ConcurrentQueue<JsonElement>(); var snapshots = new ConcurrentQueue<JsonElement>(); var foreign = new ConcurrentQueue<JsonElement>();
        bob.On<JsonElement>("NewNotification", events.Enqueue); bob.On<JsonElement>("NotificationsLoaded", snapshots.Enqueue); alice.On<JsonElement>("NewNotification", foreign.Enqueue); await Task.WhenAll(bob.StartAsync(), alice.StartAsync()); var first = await Create(f); await Until(() => events.Count == 1); await bob.StopAsync(); var missed = await Create(f); await bob.StartAsync(); await bob.InvokeAsync("GetNotifications", 20, 0); await Until(() => snapshots.Count == 1);
        var ids = snapshots.Single().GetProperty("notifications").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList(); Assert.Contains(first.Id, ids); Assert.Contains(missed.Id, ids); Assert.Equal(2, snapshots.Single().GetProperty("totalCount").GetInt32()); Assert.Equal(2, snapshots.Single().GetProperty("unreadCount").GetInt32());
        await Create(f); await Until(() => events.Count == 2); Assert.Empty(foreign); await bob.StopAsync(); await Create(f); await Task.Delay(150); Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task HubReadEventsAndCanonicalAckConvergeAllRecipientTabs()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var n = await Create(f); await using var one = f.Hub("/notification-hub", f.Bob); await using var two = f.Hub("/notification-hub", f.Bob); var changed = new ConcurrentQueue<JsonElement>(); var reads = new ConcurrentQueue<string>(); two.On<JsonElement>("NotificationsChanged", changed.Enqueue); two.On<string>("NotificationMarkedRead", reads.Enqueue); await Task.WhenAll(one.StartAsync(), two.StartAsync());
        var ack = await one.InvokeAsync<JsonElement>("MarkAsRead", n.Id); Assert.Equal(n.Id, ack.GetProperty("notification").GetProperty("id").GetString()); Assert.Equal("Read", ack.GetProperty("notification").GetProperty("status").GetString()); Assert.Equal(0, ack.GetProperty("unreadCount").GetInt32()); await Until(() => changed.Count == 1 && reads.Count == 1); Assert.Equal(f.Bob, changed.Single().GetProperty("userId").GetString()); Assert.Equal(n.Id, reads.Single());
        await one.InvokeAsync("MarkAsRead", n.Id); await Task.Delay(150); Assert.Single(changed); Assert.Single(reads);
    }

    [Fact]
    public async Task LiveTransportFailureCannotReportFailedCommittedReadOrDismiss()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var n = await Create(f); var transport = new BrokenHub();
        using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IHubContext<NotificationHub>>(); services.AddSingleton<IHubContext<NotificationHub>>(transport); })); using var bob = app.CreateClient(); bob.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Token(f.Bob));
        var read = await bob.PostAsync($"/api/notifications/{n.Id}/read", null); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal(NotificationStatus.Read, (await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync()).Status); Assert.Equal(0, (await Object(read))["unreadCount"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.OK, (await bob.DeleteAsync($"/api/notifications/{n.Id}")).StatusCode); Assert.NotNull((await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync()).DismissedAt); Assert.True(transport.Attempts >= 3);
    }

    [Fact]
    public async Task DelayedReadAcknowledgementUsesSameCurrentSnapshotAfterConcurrentHandleOrDismiss()
    {
        foreach (var dismiss in new[] { false, true })
        {
            using var f = new UserFactory(); await f.InitializeAsync(); var n = await Create(f); var transport = new PausedReadHub();
            using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IHubContext<NotificationHub>>(); services.AddSingleton<IHubContext<NotificationHub>>(transport); })); using var bob = app.CreateClient(); bob.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Token(f.Bob));
            var pendingRead = bob.PostAsync($"/api/notifications/{n.Id}/read", null);
            await transport.ReadCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(NotificationStatus.Read, (await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync()).Status);
            var terminal = dismiss ? await bob.DeleteAsync($"/api/notifications/{n.Id}") : await bob.PostAsync($"/api/notifications/{n.Id}/handle", null);
            Assert.Equal(HttpStatusCode.OK, terminal.StatusCode); transport.Release.TrySetResult();
            var response = await pendingRead.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(HttpStatusCode.OK, response.StatusCode); var ack = await Object(response);
            Assert.False(ack["synchronizationPending"]!.GetValue<bool>()); Assert.Equal(0, ack["unreadCount"]!.GetValue<int>());
            Assert.Equal(dismiss ? 0 : 1, ack["totalCount"]!.GetValue<int>());
            if (dismiss) { Assert.Null(ack["notification"]); Assert.NotNull((await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync()).DismissedAt); }
            else { Assert.Equal("Handled", ack["notification"]!["status"]!.GetValue<string>()); Assert.Equal(NotificationStatus.Handled, (await f.Db.Notifications.Find(x => x.Id == n.Id).SingleAsync()).Status); }
        }
    }

    [Fact]
    public async Task CleanupAndInvalidIdentifiersRespectRecipientAndRetention()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob); var old = Row(f); old.Status = NotificationStatus.Handled; old.HandledAt = f.Clock.Value.UtcDateTime.AddDays(-40); var keep = Row(f); keep.Status = NotificationStatus.Read; keep.ReadAt = f.Clock.Value.UtcDateTime.AddDays(-40); var foreign = Row(f, target: f.Mallory); foreign.Status = NotificationStatus.Handled; foreign.HandledAt = old.HandledAt; await f.Db.Notifications.InsertManyAsync(new[] { old, keep, foreign });
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.DeleteAsync($"/api/notifications/{f.Bob}/cleanup?daysOld=0")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await bob.DeleteAsync($"/api/notifications/{f.Bob}/cleanup?daysOld=30")).StatusCode); Assert.False(await f.Db.Notifications.Find(x => x.Id == old.Id).AnyAsync()); Assert.True(await f.Db.Notifications.Find(x => x.Id == keep.Id).AnyAsync()); Assert.True(await f.Db.Notifications.Find(x => x.Id == foreign.Id).AnyAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsync("/api/notifications/not-an-id/read", null)).StatusCode); Assert.Equal(HttpStatusCode.BadRequest, (await bob.DeleteAsync("/api/notifications/not-an-id")).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/notifications/{ObjectId.GenerateNewId()}/read", null)).StatusCode);
    }

    [Fact]
    public async Task FollowActionsRevalidateLaterPrivacyInSnapshotAcknowledgementAndPublication()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/follows", new { followedId = f.Bob })).StatusCode);
        var n = await f.Db.Notifications.Find(x => x.Type == NotificationType.Follow).SingleAsync();
        var before = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}")); Assert.Equal($"/user/{f.Alice}", before["notifications"]!.AsArray().Single()!["actionUrl"]!.GetValue<string>());
        await f.Db.Users.UpdateOneAsync(x => x.IdentityUserId == f.Alice, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        var after = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}")); Assert.Equal($"/user/{f.Bob}", after["notifications"]!.AsArray().Single()!["actionUrl"]!.GetValue<string>());
        var ack = await Object(await bob.PostAsync($"/api/notifications/{n.Id}/read", null)); Assert.Equal($"/user/{f.Bob}", ack["notification"]!["actionUrl"]!.GetValue<string>());
        await using var hub = f.Hub("/notification-hub", f.Bob); var events = new ConcurrentQueue<JsonElement>(); hub.On<JsonElement>("NewNotification", events.Enqueue); await hub.StartAsync();
        using var scope = f.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<INotificationService>().PublishAsync(n); await Until(() => events.Count == 1);
        Assert.Equal($"/user/{f.Bob}", events.Single().GetProperty("actionUrl").GetString()); Assert.Equal("Read", events.Single().GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/users/identity/{f.Alice}")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/users/identity/{f.Bob}")).StatusCode);
    }

    private sealed class PausedReadHub : IHubContext<NotificationHub>, IHubClients, IClientProxy, IGroupManager
    {
        public TaskCompletionSource ReadCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IHubClients Clients => this; public IGroupManager Groups => this; public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> except) => this; public IClientProxy Client(string connectionId) => this; IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> ids) => this; public IClientProxy Group(string group) => this; public IClientProxy GroupExcept(string group, IReadOnlyList<string> except) => this; IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groups) => this; public IClientProxy User(string user) => this; public IClientProxy Users(IReadOnlyList<string> users) => this;
        public async Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) { if (method == "NotificationMarkedRead") { ReadCommitted.TrySetResult(); await Release.Task.WaitAsync(ct); } }
        public Task AddToGroupAsync(string connection, string group, CancellationToken ct = default) => Task.CompletedTask; public Task RemoveFromGroupAsync(string connection, string group, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BrokenHub : IHubContext<NotificationHub>, IHubClients, IClientProxy, IGroupManager
    {
        public int Attempts; public IHubClients Clients => this; public IGroupManager Groups => this; public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> except) => this; public IClientProxy Client(string connectionId) => this; IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> ids) => this; public IClientProxy Group(string group) => this; public IClientProxy GroupExcept(string group, IReadOnlyList<string> except) => this; IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groups) => this; public IClientProxy User(string user) => this; public IClientProxy Users(IReadOnlyList<string> users) => this;
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) { Interlocked.Increment(ref Attempts); throw new IOException("Injected unavailable live transport"); }
        public Task AddToGroupAsync(string connection, string group, CancellationToken ct = default) => Task.CompletedTask; public Task RemoveFromGroupAsync(string connection, string group, CancellationToken ct = default) => Task.CompletedTask;
    }
}
