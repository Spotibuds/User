using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
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

public class NotificationProducerRegressionTests
{
    private static async Task<JsonObject> Object(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }
    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(25);
        Assert.True(predicate(), "Expected committed live state within the bounded test window.");
    }
    private static async Task<string> Chat(UserFactory f, HttpClient client) => (await Object(await client.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = new[] { f.Alice, f.Bob } })))["id"]!.GetValue<string>();
    private static async Task Send(HttpClient client, string chat, string content) => await Object(await client.PostAsJsonAsync($"/api/chats/{chat}/messages", new { content, clientMessageId = Guid.NewGuid().ToString() }));
    private static async Task<string> Post(UserFactory f, HttpClient author, string account)
    {
        Assert.Equal(HttpStatusCode.OK, (await author.PostAsJsonAsync($"/api/users/{account}/listening-history", new { songId = UserFactory.Song, songTitle = "forged title", artist = "forged artist", duration = 12 })).StatusCode);
        return (await f.Db.Feed.Find(x => x.IdentityUserId == account && x.SongId == UserFactory.Song).SingleAsync()).Id;
    }
    private static Task<HttpResponseMessage> Reaction(HttpClient actor, string post, string emoji = "👍") => actor.PostAsJsonAsync("/api/feed/reactions", new { postId = post, emoji, fromIdentityUserId = Guid.NewGuid().ToString(), fromUserName = "forged", toIdentityUserId = Guid.NewGuid().ToString(), songTitle = "forged", artist = "forged", contextType = "forged" });
    private static Task<HttpResponseMessage> Unfollow(HttpClient actor, string target) => actor.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/follows") { Content = JsonContent.Create(new { followedId = target }) });

    [Fact]
    public async Task ActivityIsConnectionScopedAndHiddenChatsStillPersistAndDeliverEveryMessage()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice);
        var chat = await Chat(f, alice);
        await using var hidden = f.Hub("/chat-hub", f.Bob); await using var visible = f.Hub("/chat-hub", f.Bob); await using var noticesHub = f.Hub("/notification-hub", f.Bob);
        var delivered = new ConcurrentQueue<JsonElement>(); var notices = new ConcurrentQueue<JsonElement>();
        hidden.On<JsonElement>("ReceiveMessage", delivered.Enqueue); noticesHub.On<JsonElement>("NewNotification", notices.Enqueue);
        await hidden.StartAsync(); await visible.StartAsync(); await noticesHub.StartAsync();
        await hidden.InvokeAsync("JoinChatWithVisibility", chat, false); await visible.InvokeAsync("JoinChatWithVisibility", chat, true);
        await Send(alice, chat, "visible peer suppresses attention only");
        await Until(() => delivered.Count == 1); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true));
        await visible.InvokeAsync("SetChatActive", chat, false);
        await Send(alice, chat, "all views hidden"); await Until(() => notices.Count == 1 && delivered.Count == 2);
        await hidden.InvokeAsync("SetChatActive", chat, true); await visible.StopAsync();
        await Send(alice, chat, "closing another tab keeps active view"); await Until(() => delivered.Count == 3);
        Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true));
        await hidden.StopAsync(); await Send(alice, chat, "last tab disconnected"); await Until(() => notices.Count == 2);
        await using var reconnected = f.Hub("/chat-hub", f.Bob); reconnected.On<JsonElement>("ReceiveMessage", delivered.Enqueue);
        await reconnected.StartAsync(); await reconnected.InvokeAsync("JoinChatWithVisibility", chat, false);
        await Send(alice, chat, "reconnected hidden view"); await Until(() => delivered.Count == 4 && notices.Count == 3);
        await using var outsider = f.Hub("/chat-hub", f.Mallory); await outsider.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => outsider.InvokeAsync("SetChatActive", chat, true));
        await reconnected.InvokeAsync("SetChatActive", chat, true); await Send(alice, chat, "active after visibility return"); await Until(() => delivered.Count == 5);
        Assert.Equal(6, await f.Db.Messages.CountDocumentsAsync(x => x.ChatId == chat));
        Assert.Equal(3, await f.Db.Notifications.CountDocumentsAsync(x => x.TargetUserId == f.Bob && x.Type == NotificationType.Message));
        Assert.All(notices, n => { Assert.Equal(f.Bob, n.GetProperty("targetUserId").GetString()); Assert.Equal(f.Alice, n.GetProperty("sourceUserId").GetString()); });
        Assert.Equal(5, delivered.Select(x => x.GetProperty("id").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task ChatDeletionRemovesDeadMessageNoticesAndPublishesAuthoritativeInboxInvalidation()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); using var mallory = f.Client(f.Mallory);
        var chat = await Chat(f, alice); await Send(alice, chat, "attention before deletion");
        await using var hub = f.Hub("/notification-hub", f.Bob); var counts = new ConcurrentQueue<int>(); var changed = new ConcurrentQueue<JsonElement>();
        hub.On<int>("UnreadCountUpdate", counts.Enqueue); hub.On<JsonElement>("NotificationsChanged", changed.Enqueue); await hub.StartAsync(); await Until(() => counts.Contains(1));
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.DeleteAsync($"/api/chats/{chat}")).StatusCode);
        Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true));
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/chats/{chat}")).StatusCode); await Until(() => counts.Contains(0) && !changed.IsEmpty);
        Assert.Equal(0, await f.Db.Messages.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Chats.CountDocumentsAsync(_ => true));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/chats/{chat}")).StatusCode);
        Assert.All(changed, n => Assert.Equal(f.Bob, n.GetProperty("userId").GetString()));
    }

    [Fact]
    public async Task ConcurrentFollowNotifiesOnlyActualRecipientAndRepeatedTogglesDoNotSpam()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice);
        await using var bobHub = f.Hub("/notification-hub", f.Bob); await using var malloryHub = f.Hub("/notification-hub", f.Mallory);
        var bobEvents = new ConcurrentQueue<JsonElement>(); var malloryEvents = new ConcurrentQueue<JsonElement>(); var changed = new ConcurrentQueue<JsonElement>();
        bobHub.On<JsonElement>("NewNotification", bobEvents.Enqueue); bobHub.On<JsonElement>("NotificationsChanged", changed.Enqueue); malloryHub.On<JsonElement>("NewNotification", malloryEvents.Enqueue); await bobHub.StartAsync(); await malloryHub.StartAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => alice.PostAsJsonAsync("/api/follows", new { followerId = f.Mallory, followedId = f.Bob.ToUpperInvariant() })));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode)); await Until(() => bobEvents.Count == 1);
        var edge = await f.Db.Follows.Find(_ => true).SingleAsync(); Assert.Equal(f.Alice, edge.FollowerId); Assert.Equal(f.Bob, edge.FollowedId);
        var notice = await f.Db.Notifications.Find(_ => true).SingleAsync(); Assert.Equal(NotificationType.Follow, notice.Type); Assert.Equal(f.Alice, notice.SourceUserId); Assert.Equal(f.Bob, notice.TargetUserId); Assert.Equal($"/user/{f.Alice}", notice.ActionUrl);
        Assert.Equal(HttpStatusCode.OK, (await Unfollow(alice, f.Bob)).StatusCode); await Until(() => changed.Count >= 2);
        var handled = await f.Db.Notifications.Find(_ => true).SingleAsync(); Assert.Equal(NotificationStatus.Handled, handled.Status); Assert.NotNull(handled.HandledAt);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/follows", new { followedId = f.Bob })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Unfollow(alice, f.Bob)).StatusCode); await Task.Delay(100);
        Assert.Equal(handled.HandledAt, (await f.Db.Notifications.Find(_ => true).SingleAsync()).HandledAt); Assert.Single(bobEvents); Assert.Empty(malloryEvents);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/follows", new { followedId = f.Alice })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync("/api/follows", new { followedId = Guid.NewGuid().ToString() })).StatusCode);
        Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task PrivateFollowerNoticeLinksRecipientVisibleProfileWithoutWeakeningPrivacy()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        await f.Db.Users.UpdateOneAsync(x => x.IdentityUserId == f.Alice, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/follows", new { followedId = f.Bob })).StatusCode);
        var notice = await f.Db.Notifications.Find(_ => true).SingleAsync(); Assert.Equal($"/user/{f.Bob}", notice.ActionUrl); Assert.Equal(f.Alice, notice.Data["followerId"]);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/users/identity/{f.Bob}")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/users/identity/{f.Alice}")).StatusCode);
    }

    [Fact]
    public async Task ReactionUsesActualAuthorAndCanonicalMusicThenTerminalizesOnlyTheFinalRemoval()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); var post = await Post(f, bob, f.Bob);
        await using var hub = f.Hub("/notification-hub", f.Bob); var events = new ConcurrentQueue<JsonElement>(); hub.On<JsonElement>("NewNotification", events.Enqueue); await hub.StartAsync();
        Assert.Equal("added", (await Object(await Reaction(alice, post)))["action"]!.GetValue<string>()); await Until(() => events.Count == 1);
        var reaction = await f.Db.Reactions.Find(_ => true).SingleAsync(); Assert.Equal(f.Alice, reaction.FromIdentityUserId); Assert.Equal("alice", reaction.FromUserName); Assert.Equal(f.Bob, reaction.ToIdentityUserId); Assert.Equal("Test song", reaction.SongTitle); Assert.Equal("Test artist", reaction.Artist); Assert.Equal("recent_song", reaction.ContextType);
        var notice = await f.Db.Notifications.Find(_ => true).SingleAsync(); Assert.Equal(NotificationType.Reaction, notice.Type); Assert.Equal(reaction.Id, notice.Data["reactionId"]); Assert.Equal($"/feed/post/{post}", notice.ActionUrl);
        Assert.Equal(HttpStatusCode.OK, (await Reaction(alice, post, "🔥")).StatusCode);
        Assert.Equal("removed", (await Object(await Reaction(alice, post)))["action"]!.GetValue<string>());
        var remaining = await f.Db.Reactions.Find(_ => true).SingleAsync(); notice = await f.Db.Notifications.Find(_ => true).SingleAsync(); Assert.Equal(NotificationStatus.Unread, notice.Status); Assert.Equal(remaining.Id, notice.Data["reactionId"]); Assert.Equal("🔥", notice.Data["emoji"]);
        Assert.Equal("removed", (await Object(await Reaction(alice, post, "🔥")))["action"]!.GetValue<string>()); Assert.Equal(NotificationStatus.Handled, (await f.Db.Notifications.Find(_ => true).SingleAsync()).Status);
        Assert.Equal(HttpStatusCode.OK, (await Reaction(alice, post)).StatusCode); Assert.Equal(HttpStatusCode.OK, (await Reaction(bob, post, "❤️")).StatusCode); await Task.Delay(100);
        Assert.Single(events); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true)); Assert.Equal(2, await f.Db.Reactions.CountDocumentsAsync(_ => true));
        Assert.Equal(HttpStatusCode.NotFound, (await Reaction(alice, MongoDB.Bson.ObjectId.GenerateNewId().ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reaction(alice, post, "invalid")).StatusCode);
        await f.Db.Users.UpdateOneAsync(x => x.IdentityUserId == f.Bob, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        Assert.Equal(HttpStatusCode.Forbidden, (await Reaction(alice, post, "👏")).StatusCode); Assert.Equal(2, await f.Db.Reactions.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task WeeklyAndComparisonReactionsHaveValidatedRecipientVisibleDestinations()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        await Post(f, alice, f.Alice); await Post(f, bob, f.Bob); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true)); var week = HistoryService.WeekStart(f.Clock.Value.UtcDateTime).ToString("yyyyMMdd");
        foreach (var kind in new[] { "artists", "songs" })
        {
            var id = $"weekly:{kind}:{f.Bob}:{week}"; Assert.Equal(HttpStatusCode.OK, (await Reaction(alice, id)).StatusCode);
            var notice = await f.Db.Notifications.Find(x => x.Type == NotificationType.Reaction && x.Key == $"reaction:{id}:{f.Alice}:{f.Bob}").SingleAsync(); Assert.Equal(id, notice.Data["actionPostId"]); Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/feed/post?id={Uri.EscapeDataString(id)}")).StatusCode);
        }
        var common = $"common:{f.Alice}:{f.Bob}:{week}"; Assert.Equal(HttpStatusCode.OK, (await Reaction(alice, common)).StatusCode);
        var comparison = await f.Db.Notifications.Find(x => x.Key == $"reaction:{common}:{f.Alice}:{f.Bob}").SingleAsync(); var action = $"weekly:artists:{f.Bob}:{week}";
        Assert.Equal(common, comparison.Data["postId"]); Assert.Equal(action, comparison.Data["actionPostId"]); Assert.Equal($"/feed/post/{Uri.EscapeDataString(action)}", comparison.ActionUrl);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/feed/post?id={Uri.EscapeDataString(common)}")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/feed/post?id={Uri.EscapeDataString(action)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reaction(bob, common)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Reaction(alice, $"weekly:artists:{f.Mallory}:{week}")).StatusCode);
        Assert.Equal(3, await f.Db.Notifications.CountDocumentsAsync(x => x.Type == NotificationType.Reaction));
        var expires = HistoryService.WeekStart(f.Clock.Value.UtcDateTime).AddDays(90);
        Assert.All(await f.Db.Notifications.Find(x => x.Type == NotificationType.Reaction).ToListAsync(), n => Assert.Equal(expires, n.ExpiresAt));
        f.Clock.Value = new DateTimeOffset(expires.AddMilliseconds(1));
        Assert.Equal(0, await f.Services.GetRequiredService<INotificationService>().GetUnreadCountAsync(f.Bob));
        Assert.Empty(await f.Services.GetRequiredService<INotificationService>().GetUserNotificationsAsync(f.Bob));
    }

    [Fact]
    public async Task FollowAndNowPlayingReactionRollBackWithNoticeFailureButLiveFailureKeepsCommittedAcknowledgements()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var fault = new NoticeFault { Persist = true, FailPublish = true };
        using var app = Decorate(f, fault); using var alice = app.CreateClient(); using var bob = app.CreateClient(); alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Alice)); bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Bob));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alice.PostAsJsonAsync("/api/follows", new { followedId = f.Bob })).StatusCode); Assert.Equal(0, await f.Db.Follows.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true));
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync("/api/feed/nowplaying", new { songId = UserFactory.Song, isPlaying = true, positionSec = 1 })).StatusCode);
        var nowPlaying = new { postId = $"nowplaying:{f.Bob}:{UserFactory.Song}", toIdentityUserId = f.Mallory, contextType = "now_playing", songId = UserFactory.Song, songTitle = "forged", emoji = "👍" };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alice.PostAsJsonAsync("/api/feed/reactions", nowPlaying)).StatusCode); Assert.Equal(0, await f.Db.Feed.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Reactions.CountDocumentsAsync(_ => true));
        fault.Persist = false;
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/follows", new { followedId = f.Bob })).StatusCode); Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/feed/reactions", nowPlaying)).StatusCode);
        Assert.Equal(1, await f.Db.Follows.CountDocumentsAsync(_ => true)); Assert.Equal(1, await f.Db.Feed.CountDocumentsAsync(_ => true)); Assert.Equal(1, await f.Db.Reactions.CountDocumentsAsync(_ => true)); Assert.Equal(2, await f.Db.Notifications.CountDocumentsAsync(_ => true)); Assert.Equal(2, fault.PublishCalls);
    }

    [Fact]
    public async Task FriendTerminalProducersKeepCorrectDirectionAndCancellationDoesNotInventAttention()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        var cancelled = (await Object(await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Bob, sourceUserId = f.Mallory })))["requestId"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/friends/{cancelled}?pendingOnly=true")).StatusCode); var original = await f.Db.Notifications.Find(_ => true).SingleAsync(); Assert.Equal(NotificationStatus.Handled, original.Status); Assert.Equal(f.Bob, original.TargetUserId); Assert.Equal(f.Alice, original.SourceUserId);
        var declined = (await Object(await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Bob })))["requestId"]!.GetValue<string>(); Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{declined}/decline", null)).StatusCode); Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{declined}/decline", null)).StatusCode);
        var response = await f.Db.Notifications.Find(x => x.Type == NotificationType.FriendRequestDeclined).SingleAsync(); Assert.Equal(f.Alice, response.TargetUserId); Assert.Equal(f.Bob, response.SourceUserId); Assert.Equal(declined, response.Data["requestId"]);
        var accepted = (await Object(await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = f.Bob })))["requestId"]!.GetValue<string>(); Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{accepted}/accept", null)).StatusCode);
        var firstHandledAt = (await f.Db.Notifications.Find(x => x.Key == $"friend-request:{accepted}").SingleAsync()).HandledAt;
        f.Clock.Value = f.Clock.Value.AddHours(1);
        Assert.Equal(HttpStatusCode.OK, (await bob.DeleteAsync($"/api/friends/{accepted}")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await bob.DeleteAsync($"/api/friends/{accepted}")).StatusCode);
        Assert.Equal(firstHandledAt, (await f.Db.Notifications.Find(x => x.Key == $"friend-request:{accepted}").SingleAsync()).HandledAt);
        var removed = await f.Db.Notifications.Find(x => x.Type == NotificationType.FriendRemoved).SingleAsync(); Assert.Equal(f.Alice, removed.TargetUserId); Assert.Equal(f.Bob, removed.SourceUserId);
        Assert.Equal(6, await f.Db.Notifications.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(x => x.TargetUserId == f.Mallory)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(x => x.Type == NotificationType.FriendRequest && x.Status == NotificationStatus.Unread));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountCleanupSerializesWithAnAlreadyPersistingProducerAndLeavesNoOrphan(bool reaction)
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var bob = f.Client(f.Bob); var post = reaction ? await Post(f, bob, f.Bob) : "";
        var fault = new NoticeFault { Pause = true }; using var app = Decorate(f, fault); using var alice = app.CreateClient(); alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Alice));
        var producing = reaction ? Reaction(alice, post) : alice.PostAsJsonAsync("/api/follows", new { followedId = f.Bob });
        await fault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var service = app.CreateClient(); service.DefaultRequestHeaders.Add("X-Spotibuds-Service", "test-service-secret-at-least-32-bytes");
        var cleanup = service.DeleteAsync($"/api/users/internal/{f.Bob}");
        await Task.Delay(100); var prematurelyCompleted = cleanup.IsCompleted; fault.Release.TrySetResult(); Assert.False(prematurelyCompleted, "Cleanup must wait/retry against the producer's profile write.");
        Assert.Equal(HttpStatusCode.OK, (await producing).StatusCode); Assert.Equal(HttpStatusCode.NoContent, (await cleanup).StatusCode);
        Assert.Equal(0, await f.Db.Users.CountDocumentsAsync(x => x.IdentityUserId == f.Bob)); Assert.Equal(0, await f.Db.Follows.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Reactions.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Feed.CountDocumentsAsync(x => x.IdentityUserId == f.Bob));
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Decorate(UserFactory f, NoticeFault fault) => f.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
        services.RemoveAll<INotificationService>(); services.AddScoped<INotificationService>(sp => new FaultyNotifications(new NotificationService(sp.GetRequiredService<User.Data.MongoDbContext>(), sp.GetRequiredService<IHubContext<NotificationHub>>(), sp.GetRequiredService<TimeProvider>()), fault));
    }));
    [Fact]
    public async Task HistoryFinishingCatalogueLookupAfterCleanupCannotRecreateFeedOrHistory()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var gate = new PausedCatalogue();
        using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddHttpClient("Music").ConfigurePrimaryHttpMessageHandler(() => gate)));
        using var bob = app.CreateClient(); bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Bob));
        var append = bob.PostAsJsonAsync($"/api/users/{f.Bob}/listening-history", new { songId = UserFactory.Song, songTitle = "forged", artist = "forged", duration = 12 });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var service = app.CreateClient(); service.DefaultRequestHeaders.Add("X-Spotibuds-Service", "test-service-secret-at-least-32-bytes");
        var cleanup = await service.DeleteAsync($"/api/users/internal/{f.Bob}"); gate.Release.TrySetResult();
        Assert.Equal(HttpStatusCode.NoContent, cleanup.StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await append).StatusCode);
        Assert.Equal(0, await f.Db.History.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Feed.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true));
    }
    private sealed class PausedCatalogue : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/api/songs/")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = UserFactory.Song, title = "Test song", durationSec = 125, artists = new[] { new { name = "Test artist" } }, coverUrl = (string?)null }) };
        }
    }
    private sealed class NoticeFault
    {
        public bool Persist; public bool FailPublish; public bool Pause; public int PublishCalls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class FaultyNotifications(INotificationService inner, NoticeFault fault) : INotificationService
    {
        public async Task<Notification> PersistAsync(IClientSessionHandle session, Notification notification, CancellationToken ct)
        {
            if (fault.Persist) throw new IOException("Injected notice persistence failure");
            var result = await inner.PersistAsync(session, notification, ct);
            if (fault.Pause) { fault.Entered.TrySetResult(); await fault.Release.Task.WaitAsync(ct); }
            return result;
        }
        public Task PublishAsync(Notification notification) { Interlocked.Increment(ref fault.PublishCalls); return fault.FailPublish ? throw new IOException("Injected live publication failure") : inner.PublishAsync(notification); }
        public Task RefreshCountAsync(string userId) => fault.FailPublish ? throw new IOException("Injected live count failure") : inner.RefreshCountAsync(userId);
        public Task<Notification> CreateNotificationAsync(string user, NotificationType type, string title, string message, string? source = null, Dictionary<string, object>? data = null, string? url = null, string? key = null) => inner.CreateNotificationAsync(user, type, title, message, source, data, url, key);
        public Task<List<Notification>> GetUserNotificationsAsync(string user, int limit = 50, int skip = 0) => inner.GetUserNotificationsAsync(user, limit, skip);
        public Task<int> GetUnreadCountAsync(string user) => inner.GetUnreadCountAsync(user);
        public Task MarkAsReadAsync(string id, string user) => inner.MarkAsReadAsync(id, user);
        public Task MarkAsHandledAsync(string id, string user) => inner.MarkAsHandledAsync(id, user);
        public Task MarkAllAsReadAsync(string user) => inner.MarkAllAsReadAsync(user);
        public Task CleanupOldNotificationsAsync(string user, int days = 30) => inner.CleanupOldNotificationsAsync(user, days);
    }
}
