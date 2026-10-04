using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using User.Entities;
using User.Hubs;
using User.Services;
using Xunit;

namespace User.Tests;

public class FriendRequestRegressionTests
{
    private static async Task<JsonObject> Object(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    private static async Task<JsonArray> Array(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonArray>())!;
    private static async Task<string> Send(HttpClient client, string target)
    {
        using var response = await client.PostAsJsonAsync("/api/friends/request", new { targetUserId = target });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await Object(response); Assert.Equal(dto["requestId"]!.GetValue<string>(), dto["friendshipId"]!.GetValue<string>());
        return dto["requestId"]!.GetValue<string>();
    }
    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(25);
        Assert.True(predicate(), "Expected committed SignalR event was not received.");
    }

    [Fact]
    public async Task InvalidSelfAndAnonymousRequestsCannotPersistRelationships()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var anonymous = f.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Bob })).StatusCode);
        foreach (var target in new[] { "", "not-an-account", f.Alice, f.Alice.ToUpperInvariant(), "{" + f.Alice + "}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = target })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = Guid.NewGuid().ToString() })).StatusCode);
        Assert.Equal(0, await f.Db.Friends.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task DuplicateCrossedAndAcceptedRequestsAreExplicitConflicts()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        var id = await Send(alice, f.Bob.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Bob })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Alice })).StatusCode);
        Assert.Equal(1, await f.Db.Friends.CountDocumentsAsync(_ => true)); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true));
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Bob })).StatusCode);
        var stored = await f.Db.Friends.Find(_ => true).SingleAsync(); Assert.Equal(FriendStatus.Accepted, stored.Status); Assert.NotNull(stored.AcceptedAt);
        Assert.Equal(new[] { f.Bob }, (await Array(await alice.GetAsync($"/api/friends/{f.Alice}"))).Select(x => x!.GetValue<string>()));
        Assert.Equal(new[] { f.Alice }, (await Array(await bob.GetAsync($"/api/friends/{f.Bob}"))).Select(x => x!.GetValue<string>()));
        var aliceProfile = await f.Db.Users.Find(x => x.IdentityUserId == f.Alice).SingleAsync();
        Assert.Equal(new[] { f.Bob }, (await Array(await alice.GetAsync($"/api/friends/{aliceProfile.Id}"))).Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public async Task IncomingAndSentListsUseCanonicalGuidAndEnforceOwner()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); using var mallory = f.Client(f.Mallory);
        var id = await Send(alice, f.Bob);
        var sent = await Array(await alice.GetAsync($"/api/friends/sent/{f.Alice.ToUpperInvariant()}"));
        var incoming = await Array(await bob.GetAsync($"/api/friends/pending/{f.Bob}"));
        Assert.Equal(id, sent.Single()!["requestId"]!.GetValue<string>()); Assert.Equal(f.Bob, sent.Single()!["addresseeId"]!.GetValue<string>()); Assert.Equal("bob", sent.Single()!["addresseeUsername"]!.GetValue<string>());
        Assert.Equal(id, incoming.Single()!["requestId"]!.GetValue<string>()); Assert.Equal(f.Alice, incoming.Single()!["requesterId"]!.GetValue<string>()); Assert.Equal("alice", incoming.Single()!["requesterUsername"]!.GetValue<string>());
        Assert.Empty(await Array(await bob.GetAsync($"/api/friends/sent/{f.Bob}"))); Assert.Empty(await Array(await alice.GetAsync($"/api/friends/pending/{f.Alice}")));
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync($"/api/friends/sent/{f.Alice}")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync($"/api/friends/pending/{f.Bob}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/friends/status?userId1={f.Alice}&userId2={f.Bob}")).StatusCode);
        var status = await Object(await alice.GetAsync($"/api/friends/status?userId1={f.Alice}&userId2={f.Bob.ToUpperInvariant()}")); Assert.Equal("pending", status["status"]!.GetValue<string>()); Assert.Equal(f.Alice, status["requesterId"]!.GetValue<string>()); Assert.Equal(f.Bob, status["addresseeId"]!.GetValue<string>()); Assert.NotNull(status["requestedAt"]);
    }

    [Fact]
    public async Task OnlyRecipientMayRespondAndOnlySenderMayCancelPending()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); using var mallory = f.Client(f.Mallory);
        var id = await Send(alice, f.Bob);
        foreach (var suffix in new[] { "accept", "decline" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync($"/api/friends/{id}/{suffix}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await mallory.PostAsync($"/api/friends/{id}/{suffix}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsync($"/api/friends/invalid/{suffix}", null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync($"/api/friends/{id}")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await mallory.DeleteAsync($"/api/friends/{id}")).StatusCode);
        Assert.Equal(FriendStatus.Pending, (await f.Db.Friends.Find(_ => true).SingleAsync()).Status); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(n => n.Status == NotificationStatus.Unread));
    }

    [Fact]
    public async Task DeclineAndReRequestRotateIdentityEvenWithFrozenClock()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        var old = await Send(alice, f.Bob); var mongoId = (await f.Db.Friends.Find(_ => true).SingleAsync()).Id;
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{old}/decline", null)).StatusCode);
        var declined = await f.Db.Friends.Find(_ => true).SingleAsync(); Assert.Equal(FriendStatus.Declined, declined.Status); Assert.Null(declined.AcceptedAt); Assert.NotNull(declined.RespondedAt);
        var current = await Send(alice, f.Bob); Assert.NotEqual(old, current);
        Assert.Equal(mongoId, (await f.Db.Friends.Find(_ => true).SingleAsync()).Id); Assert.Equal(1, await f.Db.Friends.CountDocumentsAsync(_ => true));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/friends/{old}/accept", null)).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/api/friends/{old}")).StatusCode);
        var requests = await f.Db.Notifications.Find(n => n.Type == NotificationType.FriendRequest).ToListAsync(); Assert.Equal(2, requests.Count); Assert.Single(requests, n => n.Status == NotificationStatus.Handled); Assert.Single(requests, n => n.Status == NotificationStatus.Unread);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{current}/accept", null)).StatusCode);
        Assert.Equal(2, await f.Db.Notifications.CountDocumentsAsync(n => n.Type == NotificationType.FriendRequest && n.Status == NotificationStatus.Handled));
    }

    [Fact]
    public async Task CancellationClearsActionableNotificationAndCannotBeAccepted()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        var id = await Send(alice, f.Bob);
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/friends/{id}?pendingOnly=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/friends/{id}?pendingOnly=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsync($"/api/friends/{id}/accept", null)).StatusCode);
        Assert.Equal(FriendStatus.Cancelled, (await f.Db.Friends.Find(_ => true).SingleAsync()).Status); Assert.Equal(NotificationStatus.Handled, (await f.Db.Notifications.Find(_ => true).SingleAsync()).Status);
        Assert.Empty(await Array(await alice.GetAsync($"/api/friends/sent/{f.Alice}"))); Assert.Empty(await Array(await bob.GetAsync($"/api/friends/pending/{f.Bob}")));
        var notices = await Object(await bob.GetAsync($"/api/notifications/{f.Bob}")); Assert.Equal(0, notices["unreadCount"]!.GetValue<int>());
        Assert.Equal("none", (await Object(await alice.GetAsync($"/api/friends/status?userId1={f.Alice}&userId2={f.Bob}")))["status"]!.GetValue<string>());
        Assert.NotEqual(id, await Send(alice, f.Bob)); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(n => n.Status == NotificationStatus.Unread));
    }

    [Fact]
    public async Task ConcurrentCrossedSubmissionsPersistOneDirectedRequestAndOneNotice()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Bob }) : bob.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Alice })));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK); Assert.Equal(11, results.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        var friend = await f.Db.Friends.Find(_ => true).SingleAsync(); Assert.Equal(FriendStatus.Pending, friend.Status);
        var notice = await f.Db.Notifications.Find(_ => true).SingleAsync(); var sender = await f.Db.Users.Find(x => x.Id == friend.UserId).SingleAsync(); var recipient = await f.Db.Users.Find(x => x.Id == friend.FriendId).SingleAsync();
        Assert.Equal(sender.IdentityUserId, notice.SourceUserId); Assert.Equal(recipient.IdentityUserId, notice.TargetUserId); Assert.Equal(SocialCommands.PublicId(friend), notice.Data["requestId"]);
    }

    [Fact]
    public async Task ConcurrentAcceptDeclineHaveOneTerminalWinnerAndNotification()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); var id = await Send(alice, f.Bob);
        var results = await Task.WhenAll(bob.PostAsync($"/api/friends/{id}/accept", null), bob.PostAsync($"/api/friends/{id}/decline", null));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        var friend = await f.Db.Friends.Find(_ => true).SingleAsync(); Assert.Contains(friend.Status, new[] { FriendStatus.Accepted, FriendStatus.Declined });
        Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(n => n.Type == NotificationType.FriendRequest && n.Status == NotificationStatus.Handled)); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(n => n.Type == NotificationType.FriendRequestAccepted || n.Type == NotificationType.FriendRequestDeclined));
        Assert.Equal(friend.Status == FriendStatus.Accepted, friend.AcceptedAt != null);
    }

    [Fact]
    public async Task ConcurrentCancellationCannotRemoveAcceptedFriendship()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); var id = await Send(alice, f.Bob);
        var results = await Task.WhenAll(alice.DeleteAsync($"/api/friends/{id}?pendingOnly=true"), bob.PostAsync($"/api/friends/{id}/accept", null));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        var friend = await f.Db.Friends.Find(_ => true).SingleAsync(); Assert.Contains(friend.Status, new[] { FriendStatus.Accepted, FriendStatus.Cancelled }); Assert.NotEqual(FriendStatus.Removed, friend.Status);
        Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(n => n.Type == NotificationType.FriendRequest && n.Status == NotificationStatus.Handled));
        Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(n => n.Type == NotificationType.FriendRemoved));
    }

    [Fact]
    public async Task CommittedCanonicalEventsReachBothTabsOnceAndNeverThirdParty()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        await using var aHub = f.Hub("/friend-hub", f.Alice); await using var aTab = f.Hub("/friend-hub", f.Alice); await using var bHub = f.Hub("/friend-hub", f.Bob); await using var mHub = f.Hub("/friend-hub", f.Mallory); await using var nHub = f.Hub("/notification-hub", f.Alice);
        var received = new ConcurrentQueue<JsonElement>(); var acceptedA = new ConcurrentQueue<JsonElement>(); var acceptedTab = new ConcurrentQueue<JsonElement>(); var acceptedB = new ConcurrentQueue<JsonElement>(); var thirdParty = new ConcurrentQueue<JsonElement>(); var notices = new ConcurrentQueue<JsonElement>();
        bHub.On<JsonElement>("FriendRequestReceived", received.Enqueue); aHub.On<JsonElement>("FriendRequestAccepted", acceptedA.Enqueue); aTab.On<JsonElement>("FriendRequestAccepted", acceptedTab.Enqueue); bHub.On<JsonElement>("FriendRequestAccepted", acceptedB.Enqueue); mHub.On<JsonElement>("FriendRequestReceived", thirdParty.Enqueue); mHub.On<JsonElement>("FriendRequestAccepted", thirdParty.Enqueue); nHub.On<JsonElement>("NewNotification", notices.Enqueue);
        await Task.WhenAll(aHub.StartAsync(), aTab.StartAsync(), bHub.StartAsync(), mHub.StartAsync(), nHub.StartAsync());
        var id = await Send(alice, f.Bob); await Until(() => received.Count == 1);
        var request = received.Single(); Assert.Equal(id, request.GetProperty("requestId").GetString()); Assert.Equal(f.Alice, request.GetProperty("requesterId").GetString()); Assert.Equal("alice", request.GetProperty("requesterUsername").GetString()); Assert.True(request.TryGetProperty("requestedAt", out _));
        await bHub.InvokeAsync("AcceptFriendRequest", id); await Until(() => acceptedA.Count == 1 && acceptedTab.Count == 1 && acceptedB.Count == 1 && notices.Count == 1);
        Assert.Equal(f.Bob, acceptedA.Single().GetProperty("friendId").GetString()); Assert.Equal(f.Alice, acceptedB.Single().GetProperty("friendId").GetString()); Assert.Equal(id, acceptedB.Single().GetProperty("requestId").GetString());
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{id}/accept", null)).StatusCode); await bHub.InvokeAsync("AcceptFriendRequest", id); await Task.Delay(200);
        Assert.Single(acceptedA); Assert.Single(acceptedTab); Assert.Single(acceptedB); Assert.Single(notices); Assert.Empty(thirdParty);
        Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(n => n.Type == NotificationType.FriendRequestAccepted));
    }

    [Fact]
    public async Task HubCancellationAndEitherMemberRemovalSyncBothParticipants()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        await using var a = f.Hub("/friend-hub", f.Alice); await using var b = f.Hub("/friend-hub", f.Bob);
        var cancelA = new ConcurrentQueue<JsonElement>(); var cancelB = new ConcurrentQueue<JsonElement>(); var removedA = new ConcurrentQueue<JsonElement>(); var removedB = new ConcurrentQueue<JsonElement>();
        a.On<JsonElement>("FriendRequestCancelled", cancelA.Enqueue); b.On<JsonElement>("FriendRequestCancelled", cancelB.Enqueue); a.On<JsonElement>("FriendRemoved", removedA.Enqueue); b.On<JsonElement>("FriendRemoved", removedB.Enqueue);
        await Task.WhenAll(a.StartAsync(), b.StartAsync()); var cancelled = await Send(alice, f.Bob); await Assert.ThrowsAsync<HubException>(() => b.InvokeAsync("CancelFriendRequest", cancelled)); await a.InvokeAsync("CancelFriendRequest", cancelled); await Until(() => cancelA.Count == 1 && cancelB.Count == 1);
        Assert.Equal(f.Bob, cancelA.Single().GetProperty("friendId").GetString()); Assert.Equal(f.Alice, cancelB.Single().GetProperty("friendId").GetString());
        var accepted = await Send(alice, f.Bob); await b.InvokeAsync("AcceptFriendRequest", accepted); await Assert.ThrowsAsync<HubException>(() => a.InvokeAsync("CancelFriendRequest", accepted));
        await b.InvokeAsync("RemoveFriend", f.Alice); await Until(() => removedA.Count == 1 && removedB.Count == 1);
        Assert.Equal(f.Bob, removedA.Single().GetProperty("friendId").GetString()); Assert.Equal(f.Alice, removedB.Single().GetProperty("friendId").GetString());
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/friends/{accepted}")).StatusCode); await Task.Delay(150); Assert.Single(removedA); Assert.Single(removedB);
        Assert.Empty(await Array(await alice.GetAsync($"/api/friends/{f.Alice}"))); Assert.Empty(await Array(await bob.GetAsync($"/api/friends/{f.Bob}"))); Assert.Equal(FriendStatus.Removed, (await f.Db.Friends.Find(_ => true).SingleAsync()).Status);
        Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(n => n.Type == NotificationType.FriendRemoved)); Assert.NotEqual(accepted, await Send(alice, f.Bob));
    }

    [Fact]
    public async Task LegacyRequestIdsRemainUsableUntilNextAttemptWithoutHandlingUnrelatedNotice()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        var sender = await f.Db.Users.Find(x => x.IdentityUserId == f.Alice).SingleAsync(); var recipient = await f.Db.Users.Find(x => x.IdentityUserId == f.Bob).SingleAsync();
        var legacy = new Friend { UserId = sender.Id, FriendId = recipient.Id, PairKey = SocialCommands.Pair(sender.Id, recipient.Id) }; await f.Db.Friends.InsertOneAsync(legacy);
        var actionable = new Notification { TargetUserId = f.Bob, SourceUserId = f.Alice, Type = NotificationType.FriendRequest, Key = "legacy-unique", Data = new() { ["requestId"] = legacy.Id } };
        var unrelated = new Notification { TargetUserId = f.Bob, SourceUserId = f.Alice, Type = NotificationType.FriendRequest, Key = "unrelated", Data = new() { ["requestId"] = MongoDB.Bson.ObjectId.GenerateNewId().ToString() } }; await f.Db.Notifications.InsertManyAsync(new[] { actionable, unrelated });
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{legacy.Id}/decline", null)).StatusCode);
        Assert.Equal(NotificationStatus.Handled, (await f.Db.Notifications.Find(x => x.Id == actionable.Id).SingleAsync()).Status); Assert.Equal(NotificationStatus.Unread, (await f.Db.Notifications.Find(x => x.Id == unrelated.Id).SingleAsync()).Status);
        var current = await Send(alice, f.Bob); Assert.NotEqual(legacy.Id, current); Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/friends/{legacy.Id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task NoticePersistenceFailureRollsBackAndLiveFailureDoesNotInvalidateCommit()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var fault = new NoticeFault();
        using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<INotificationService>();
            services.AddScoped<INotificationService>(sp => new FaultyNotifications(new NotificationService(sp.GetRequiredService<User.Data.MongoDbContext>(), sp.GetRequiredService<IHubContext<NotificationHub>>(), sp.GetRequiredService<TimeProvider>()), fault));
        }));
        using var alice = app.CreateClient(); using var bob = app.CreateClient();
        alice.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Token(f.Alice));
        bob.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Token(f.Bob));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Bob })).StatusCode);
        Assert.Equal(0, await f.Db.Friends.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true));
        fault.Persist = false; var id = await Send(alice, f.Bob); Assert.Equal(1, fault.PublishCalls);
        fault.Persist = true; Assert.Equal(HttpStatusCode.ServiceUnavailable, (await bob.PostAsync($"/api/friends/{id}/accept", null)).StatusCode);
        Assert.Equal(FriendStatus.Pending, (await f.Db.Friends.Find(_ => true).SingleAsync()).Status); Assert.Equal(NotificationStatus.Unread, (await f.Db.Notifications.Find(_ => true).SingleAsync()).Status);
        fault.Persist = false; Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{id}/accept", null)).StatusCode); Assert.Equal(2, fault.PublishCalls); Assert.Equal(FriendStatus.Accepted, (await f.Db.Friends.Find(_ => true).SingleAsync()).Status);
        fault.Persist = true; Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alice.DeleteAsync($"/api/friends/{id}")).StatusCode); Assert.Equal(FriendStatus.Accepted, (await f.Db.Friends.Find(_ => true).SingleAsync()).Status); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(x => x.Type == NotificationType.FriendRemoved));
        fault.Persist = false; Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/friends/{id}")).StatusCode); Assert.Equal(3, fault.PublishCalls); Assert.Equal(FriendStatus.Removed, (await f.Db.Friends.Find(_ => true).SingleAsync()).Status);
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/friends/{id}")).StatusCode); Assert.Equal(3, fault.PublishCalls); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(x => x.Type == NotificationType.FriendRemoved));
    }

    private sealed class NoticeFault { public bool Persist = true; public int PublishCalls; }
    private sealed class FaultyNotifications(INotificationService inner, NoticeFault fault) : INotificationService
    {
        public Task<Notification> PersistAsync(IClientSessionHandle session, Notification notification, CancellationToken ct) => fault.Persist ? throw new IOException("Injected unavailable notification persistence") : inner.PersistAsync(session, notification, ct);
        public Task PublishAsync(Notification notification) { Interlocked.Increment(ref fault.PublishCalls); throw new IOException("Injected unavailable live notification transport"); }
        public Task RefreshCountAsync(string userId) => throw new IOException("Injected unavailable live notification count");
        public Task<Notification> CreateNotificationAsync(string userId, NotificationType type, string title, string message, string? source = null, Dictionary<string, object>? data = null, string? url = null, string? key = null) => inner.CreateNotificationAsync(userId, type, title, message, source, data, url, key);
        public Task<List<Notification>> GetUserNotificationsAsync(string userId, int limit = 50, int skip = 0) => inner.GetUserNotificationsAsync(userId, limit, skip);
        public Task<int> GetUnreadCountAsync(string userId) => inner.GetUnreadCountAsync(userId);
        public Task MarkAsReadAsync(string id, string userId) => inner.MarkAsReadAsync(id, userId);
        public Task MarkAsHandledAsync(string id, string userId) => inner.MarkAsHandledAsync(id, userId);
        public Task MarkAllAsReadAsync(string userId) => inner.MarkAllAsReadAsync(userId);
        public Task CleanupOldNotificationsAsync(string userId, int days = 30) => inner.CleanupOldNotificationsAsync(userId, days);
    }

    [Fact]
    public async Task FollowGuidNormalizationAndObjectIdReadsUsePersistedCanonicalEdges()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        var bobProfile = await f.Db.Users.Find(x => x.IdentityUserId == f.Bob).SingleAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/follows", new { followedId = f.Alice.ToUpperInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/follows", new { followerId = f.Mallory, followedId = f.Bob.ToUpperInvariant() })).StatusCode);
        var edge = await f.Db.Follows.Find(_ => true).SingleAsync(); Assert.Equal(f.Alice, edge.FollowerId); Assert.Equal(f.Bob, edge.FollowedId);
        Assert.True((await alice.GetFromJsonAsync<bool>($"/api/follows/check?followerId={f.Alice.ToUpperInvariant()}&followedId={f.Bob.ToUpperInvariant()}")));
        Assert.Equal(new[] { bobProfile.Id }, (await Array(await alice.GetAsync($"/api/follows/{f.Alice}/following"))).Select(x => x!.GetValue<string>()));
        var stats = await Object(await bob.GetAsync($"/api/follows/{bobProfile.Id}/stats")); Assert.Equal(f.Bob, stats["userId"]!.GetValue<string>()); Assert.Equal(1, stats["followerCount"]!.GetValue<int>());
        Assert.Single(await Array(await bob.GetAsync($"/api/follows/{bobProfile.Id}/followers")));
        using var deleted = await alice.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/follows") { Content = JsonContent.Create(new { followedId = f.Bob.ToUpperInvariant() }) }); Assert.Equal(HttpStatusCode.OK, deleted.StatusCode); Assert.Equal(0, await f.Db.Follows.CountDocumentsAsync(_ => true));
    }
}
