using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using User.Entities;
using User.Hubs;
using User.Services;
using Xunit;

namespace User.Tests;

public class ChatRegressionTests
{
    private static async Task<JsonObject> Object(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }
    private static async Task<string> Create(UserFactory f, HttpClient actor, params string[] participants) =>
        (await Object(await actor.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = participants.Length == 0 ? new[] { f.Alice, f.Bob } : participants, isGroup = participants.Length > 2 })))["id"]!.GetValue<string>();
    private static async Task<JsonObject> Send(HttpClient client, string chat, string content, string? nonce = null) =>
        await Object(await client.PostAsJsonAsync($"/api/chats/{chat}/messages", new { content, clientMessageId = nonce ?? Guid.NewGuid().ToString() }));
    private static async Task<JsonArray> History(HttpClient client, string chat, string query = "") =>
        (await (await client.GetAsync($"/api/chats/{chat}/messages{query}")).Content.ReadFromJsonAsync<JsonArray>())!;
    private static async Task Until(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++) { if (predicate()) return; await Task.Delay(20); }
        Assert.True(predicate(), "Expected live event/state within two seconds.");
    }

    [Fact]
    public async Task EqualTimestampRetryKeepsLatestPreviewAndPublishesOnlyTheOriginalWrites()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice);
        f.Clock.Value = DateTimeOffset.FromUnixTimeMilliseconds(f.Clock.Value.ToUnixTimeMilliseconds());
        var chat = await Create(f, alice);
        await using var bobHub = f.Hub("/chat-hub", f.Bob); await using var friendHub = f.Hub("/friend-hub", f.Bob); await using var noticesHub = f.Hub("/notification-hub", f.Bob);
        var received = new ConcurrentQueue<JsonElement>(); var legacy = new ConcurrentQueue<JsonElement>(); var notices = new ConcurrentQueue<JsonElement>();
        bobHub.On<JsonElement>("ReceiveMessage", received.Enqueue); friendHub.On<JsonElement>("NewMessage", legacy.Enqueue); noticesHub.On<JsonElement>("NewNotification", notices.Enqueue);
        await bobHub.StartAsync(); await friendHub.StartAsync(); await noticesHub.StartAsync();
        var nonce = Guid.NewGuid(); var first = await Send(alice, chat, "first", nonce.ToString()); var latest = await Send(alice, chat, "latest");
        await Until(() => received.Count == 2 && legacy.Count == 2 && notices.Count == 2);
        var retried = await Send(alice, chat, "first", nonce.ToString("B").ToUpperInvariant());
        Assert.Equal(first["id"]!.GetValue<string>(), retried["id"]!.GetValue<string>());
        Assert.Equal(nonce.ToString(), retried["clientMessageId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync($"/api/chats/{chat}/messages", new { content = "edited retry", clientMessageId = nonce })).StatusCode);
        await Task.Delay(150); // Negative assertion: retries must not queue another live event.
        Assert.Equal(2, received.Count); Assert.Equal(2, legacy.Count); Assert.Equal(2, notices.Count);
        Assert.Equal(2, await f.Db.Messages.CountDocumentsAsync(x => x.ChatId == chat)); Assert.Equal(2, await f.Db.Notifications.CountDocumentsAsync(x => x.Type == NotificationType.Message));
        var persisted = await f.Db.Chats.Find(x => x.Id == chat).SingleAsync(); Assert.Equal(latest["id"]!.GetValue<string>(), persisted.LastMessageId);
        var reloaded = await Object(await alice.GetAsync($"/api/chats/{chat}")); Assert.Equal("latest", reloaded["lastMessageContent"]!.GetValue<string>());
    }

    [Fact]
    public async Task CursorHistoryHasNoGapsWhenNewMessagesArriveAndRejectsForeignAnchors()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var mallory = f.Client(f.Mallory);
        var chat = await Create(f, alice); var original = new List<string>();
        for (var i = 0; i < 9; i++) original.Add((await Send(alice, chat, $"message {i}"))["id"]!.GetValue<string>());
        var page = await History(alice, chat, "?pageSize=3"); Assert.Equal(original.AsEnumerable().Reverse().Take(3), page.Select(x => x!["id"]!.GetValue<string>()));
        var all = page.Select(x => x!["id"]!.GetValue<string>()).ToList();
        f.Clock.Value = f.Clock.Value.AddMinutes(-5);
        var arrival = await Send(alice, chat, "arrived between pages");
        Assert.Equal(arrival["id"]!.GetValue<string>(), (await History(alice, chat)).First()!["id"]!.GetValue<string>());
        while (page.Count > 0)
        {
            var cursor = page.Last()!["id"]!.GetValue<string>(); page = await History(alice, chat, $"?pageSize=3&before={cursor}");
            all.AddRange(page.Select(x => x!["id"]!.GetValue<string>()));
        }
        Assert.Equal(original.AsEnumerable().Reverse(), all); Assert.Equal(9, all.Distinct().Count());
        var otherChat = await Create(f, alice, f.Alice, f.Mallory); var foreign = (await Send(alice, otherChat, "other chat"))["id"]!.GetValue<string>();
        foreach (var query in new[] { $"?before={foreign}", "?before=invalid", $"?before={original[0]}&page=2", "?page=0", "?pageSize=101" }) Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/chats/{chat}/messages{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync($"/api/chats/{chat}/messages?before={original[0]}")).StatusCode);
    }

    [Fact]
    public async Task SnapshotReadReceiptsReachUnjoinedSenderAndClearOnlyMatchingNotifications()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); using var mallory = f.Client(f.Mallory);
        var chat = await Create(f, alice); var first = await Send(alice, chat, "displayed first"); var boundary = await Send(alice, chat, "displayed last"); var unseen = await Send(alice, chat, "not displayed yet");
        await using var senderHub = f.Hub("/chat-hub", f.Alice); await using var outsiderHub = f.Hub("/chat-hub", f.Mallory); await using var senderFriend = f.Hub("/friend-hub", f.Alice); await using var outsiderFriend = f.Hub("/friend-hub", f.Mallory);
        var allReads = new ConcurrentQueue<JsonElement>(); var singleReads = new ConcurrentQueue<JsonElement>(); var leaked = new ConcurrentQueue<JsonElement>(); var friendReads = new ConcurrentQueue<JsonElement>();
        senderHub.On<JsonElement>("AllMessagesRead", allReads.Enqueue); senderHub.On<JsonElement>("MessageRead", singleReads.Enqueue); outsiderHub.On<JsonElement>("AllMessagesRead", leaked.Enqueue);
        senderFriend.On<JsonElement>("AllMessagesRead", friendReads.Enqueue); outsiderFriend.On<JsonElement>("AllMessagesRead", leaked.Enqueue);
        await senderHub.StartAsync(); await outsiderHub.StartAsync(); await senderFriend.StartAsync(); await outsiderFriend.StartAsync(); // Sender deliberately never joins a room.
        f.Clock.Value = f.Clock.Value.AddSeconds(10);
        var cutoff = boundary["id"]!.GetValue<string>();
        var saved = await Object(await bob.PostAsJsonAsync($"/api/chats/{chat}/mark-all-read", new { throughMessageId = cutoff }));
        await Until(() => allReads.Count == 1 && friendReads.Count == 1);
        Assert.Equal(cutoff, saved["throughMessageId"]!.GetValue<string>());
        Assert.Equal(chat, allReads.Single().GetProperty("chatId").GetString()); Assert.Equal(f.Bob, allReads.Single().GetProperty("userId").GetString()); Assert.Equal(cutoff, friendReads.Single().GetProperty("throughMessageId").GetString());
        var readAt = saved["readAt"]!.GetValue<DateTime>();
        var messages = await f.Db.Messages.Find(x => x.ChatId == chat).ToListAsync();
        foreach (var id in new[] { first["id"]!.GetValue<string>(), cutoff }) Assert.Equal(readAt, messages.Single(x => x.Id == id).ReadBy.Single(x => x.UserId == f.Bob).ReadAt);
        Assert.DoesNotContain(messages.Single(x => x.Id == unseen["id"]!.GetValue<string>()).ReadBy, x => x.UserId == f.Bob);
        Assert.Equal(2, await f.Db.Notifications.CountDocumentsAsync(x => x.TargetUserId == f.Bob && x.Status == NotificationStatus.Read)); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(x => x.TargetUserId == f.Bob && x.Status == NotificationStatus.Unread));
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync($"/api/chats/{chat}/mark-all-read", new { throughMessageId = cutoff })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.PostAsJsonAsync($"/api/chats/{chat}/mark-all-read", new { throughMessageId = cutoff })).StatusCode);
        var foreignChat = await Create(f, alice, f.Alice, f.Mallory); var foreign = (await Send(alice, foreignChat, "foreign boundary"))["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync($"/api/chats/{chat}/mark-all-read", new { throughMessageId = foreign })).StatusCode);
        f.Clock.Value = f.Clock.Value.AddSeconds(1); var unseenId = unseen["id"]!.GetValue<string>();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => bob.PostAsync($"/api/chats/messages/{unseenId}/read", null))); Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        await Until(() => singleReads.Count == 1); await Task.Delay(100);
        Assert.Single(allReads); Assert.Single(friendReads); Assert.Empty(leaked); Assert.Single(singleReads);
        var final = await f.Db.Messages.Find(x => x.Id == unseenId).SingleAsync(); Assert.Single(final.ReadBy, x => x.UserId == f.Bob); Assert.Equal(final.ReadBy.Single(x => x.UserId == f.Bob).ReadAt, singleReads.Single().GetProperty("readAt").GetDateTime());
        Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(x => x.TargetUserId == f.Bob && x.Status == NotificationStatus.Unread));
        Assert.All(await History(alice, chat), x => Assert.True(x!["isRead"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task GroupPeerReceiptsRemainMonotonicAndContainEveryActualReader()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); using var mallory = f.Client(f.Mallory);
        var chat = await Create(f, alice, f.Alice, f.Bob, f.Mallory); var id = (await Send(alice, chat, "group message"))["id"]!.GetValue<string>();
        // The sender uses only the legacy FriendHub surface and still receives receipts.
        await using var sender = f.Hub("/friend-hub", f.Alice); var reads = new ConcurrentQueue<JsonElement>(); sender.On<JsonElement>("MessageRead", reads.Enqueue); await sender.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/chats/messages/{id}/read", null)).StatusCode); Assert.True((await History(alice, chat)).Single()!["isRead"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await mallory.PostAsync($"/api/chats/messages/{id}/read", null)).StatusCode); await Until(() => reads.Count == 2);
        var stored = await f.Db.Messages.Find(x => x.Id == id).SingleAsync(); Assert.Equal(3, stored.ReadBy.Count); Assert.Equal(3, stored.ReadBy.Select(x => x.UserId).Distinct().Count());
        Assert.True((await History(alice, chat)).Single()!["isRead"]!.GetValue<bool>()); Assert.Equal(new[] { f.Bob, f.Mallory }.Order(), reads.Select(x => x.GetProperty("userId").GetString()).Order());
    }

    [Fact]
    public async Task InitialDeliveryAndRejoinedConnectionsUseAuthoritativeMembershipAndTabScopedActivity()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice);
        var chat = await Create(f, alice); var active = f.Services.GetRequiredService<IActiveChatTrackingService>();
        await using var tabOne = f.Hub("/chat-hub", f.Bob); await using var tabTwo = f.Hub("/chat-hub", f.Bob);
        var initial = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); tabOne.On<JsonElement>("ReceiveMessage", dto => initial.TrySetResult(dto));
        await tabOne.StartAsync(); await tabTwo.StartAsync(); var first = await Send(alice, chat, "personal delivery before room join");
        Assert.Equal(first["id"]!.GetValue<string>(), (await initial.Task.WaitAsync(TimeSpan.FromSeconds(3))).GetProperty("id").GetString());
        await tabOne.InvokeAsync("JoinChat", chat.ToUpperInvariant()); await tabTwo.InvokeAsync("JoinChat", chat); Assert.True(active.IsUserInChat(chat, f.Bob));
        await tabOne.StopAsync(); await Send(alice, chat, "remaining tab is active"); Assert.True(active.IsUserInChat(chat, f.Bob)); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(x => x.TargetUserId == f.Bob && x.Type == NotificationType.Message));
        await tabTwo.StopAsync(); await Until(() => !active.IsUserInChat(chat, f.Bob)); var offline = await Send(alice, chat, "offline catch-up"); Assert.Equal(2, await f.Db.Notifications.CountDocumentsAsync(x => x.TargetUserId == f.Bob && x.Type == NotificationType.Message));
        await using var reconnected = f.Hub("/chat-hub", f.Bob); await using var outsider = f.Hub("/chat-hub", f.Mallory); await reconnected.StartAsync(); await reconnected.InvokeAsync("JoinChat", chat); await outsider.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => outsider.InvokeAsync("JoinChat", chat)); await Assert.ThrowsAsync<HubException>(() => outsider.InvokeAsync("MarkMessagesReadThrough", chat, offline["id"]!.GetValue<string>()));
        using var bob = f.Client(f.Bob); Assert.Equal(3, (await History(bob, chat)).Count);
        var typing = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); reconnected.On<JsonElement>("UserTyping", dto => typing.TrySetResult(dto)); await using var sender = f.Hub("/chat-hub", f.Alice); await sender.StartAsync(); await sender.InvokeAsync("StartTyping", chat);
        Assert.Equal(chat, (await typing.Task.WaitAsync(TimeSpan.FromSeconds(3))).GetProperty("chatId").GetString());
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/chats/{chat}")).StatusCode); await reconnected.InvokeAsync("LeaveChat", chat); Assert.False(active.IsUserInChat(chat, f.Bob));
    }

    [Fact]
    public async Task InvalidParticipantsAndPrivateNonfriendsCannotCreateChatsOrMutateMessages()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice);
        foreach (var ids in new List<string>?[] { null, [], [f.Alice, Guid.Parse(f.Alice).ToString("B").ToUpperInvariant()], [f.Alice, "invalid"] })
            Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = ids })).StatusCode);
        await f.Db.Users.UpdateOneAsync(x => x.IdentityUserId == f.Bob, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = new[] { f.Alice, f.Bob } })).StatusCode); Assert.Equal(0, await f.Db.Chats.CountDocumentsAsync(_ => true));
        var self = await f.Db.Users.Find(x => x.IdentityUserId == f.Alice).SingleAsync(); var other = await f.Db.Users.Find(x => x.IdentityUserId == f.Bob).SingleAsync();
        await f.Db.Friends.InsertOneAsync(new Friend { UserId = self.Id, FriendId = other.Id, PairKey = SocialCommands.Pair(self.Id, other.Id), Status = FriendStatus.Accepted });
        var chat = await Create(f, alice, Guid.Parse(f.Alice).ToString("B").ToUpperInvariant(), Guid.Parse(f.Bob).ToString("N"));
        await using var hub = f.Hub("/chat-hub", f.Alice); await hub.StartAsync();
        f.Dependencies.SessionStatus = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alice.PostAsJsonAsync($"/api/chats/{chat}/messages", new { content = "must not persist" })).StatusCode);
        var failure = await Record.ExceptionAsync(() => hub.InvokeAsync<JsonElement>("SendMessage", chat, "must not persist", Guid.NewGuid().ToString()));
        Assert.NotNull(failure);
        if (failure is InvalidOperationException)
        {
            // Long-poll authentication can close the connection before invocation.
            Assert.NotEqual(HubConnectionState.Connected, hub.State); Assert.Contains("connection is not active", failure.Message);
        }
        else Assert.Contains("Identity service unavailable", Assert.IsType<HubException>(failure).Message);
        Assert.Equal(0, await f.Db.Messages.CountDocumentsAsync(_ => true));
        await hub.StopAsync(); f.Dependencies.SessionStatus = HttpStatusCode.NoContent; await hub.StartAsync();
        var recovered = await hub.InvokeAsync<JsonElement>("SendMessage", chat, "recovered hub", Guid.NewGuid().ToString()); Assert.Equal("recovered hub", recovered.GetProperty("content").GetString()); Assert.NotNull(await Send(alice, chat, "recovered REST"));
        f.Dependencies.SessionStatus = HttpStatusCode.Unauthorized; Assert.Equal(HttpStatusCode.Unauthorized, (await alice.PostAsync($"/api/chats/{chat}/mark-all-read", null)).StatusCode); Assert.Equal(2, await f.Db.Messages.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task ExistingPrivateChatSurvivesUnfriendButNewCreationSerializesAgainstRemoval()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice);
        await f.Db.Users.UpdateOneAsync(x => x.IdentityUserId == f.Bob, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        var self = await f.Db.Users.Find(x => x.IdentityUserId == f.Alice).SingleAsync(); var other = await f.Db.Users.Find(x => x.IdentityUserId == f.Bob).SingleAsync();
        var relation = new Friend { UserId = self.Id, FriendId = other.Id, PairKey = SocialCommands.Pair(self.Id, other.Id), Status = FriendStatus.Accepted };
        await f.Db.Friends.InsertOneAsync(relation); var established = await Create(f, alice);
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/friends/{relation.Id}")).StatusCode);
        Assert.Equal(established, await Create(f, alice)); Assert.NotNull(await Send(alice, established, "established membership remains valid"));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = new[] { f.Alice, f.Bob, f.Mallory }, isGroup = true })).StatusCode);
        await f.Db.Friends.UpdateOneAsync(x => x.Id == relation.Id, Builders<Friend>.Update.Set(x => x.Status, FriendStatus.Accepted));

        // Observe the real permission read, then finish a competing transaction that
        // owns the friendship write lock. This avoids timing-only race assertions.
        var permissionRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("USER_TEST_MONGO")); settings.DirectConnection = true; settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandSucceededEvent>(e =>
        {
            if (e.CommandName != "find" || !e.Reply.TryGetValue("cursor", out var cursorValue) || !cursorValue.IsBsonDocument) return;
            var cursor = cursorValue.AsBsonDocument;
            if (!cursor.TryGetValue("ns", out var ns) || ns.AsString != f.DatabaseName + ".friends" || !cursor.TryGetValue("firstBatch", out var batch)) return;
            if (batch.AsBsonArray.Any(value => value.AsBsonDocument.TryGetValue("status", out var status) && status.ToInt32() == (int)FriendStatus.Accepted)) permissionRead.TrySetResult();
        });
        using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IMongoClient>(); services.AddSingleton<IMongoClient>(new MongoClient(settings)); }));
        using var contender = app.CreateClient(); contender.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Token(f.Alice));
        using var held = await f.Services.GetRequiredService<IMongoClient>().StartSessionAsync(); held.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        await f.Db.Friends.UpdateOneAsync(held, x => x.Id == relation.Id, Builders<Friend>.Update.Set(x => x.Status, FriendStatus.Removed));
        var creating = contender.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = new[] { f.Alice, f.Bob, f.Mallory }, isGroup = true });
        await permissionRead.Task.WaitAsync(TimeSpan.FromSeconds(5)); await held.CommitTransactionAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await creating.WaitAsync(TimeSpan.FromSeconds(5))).StatusCode);
        Assert.Equal(FriendStatus.Removed, (await f.Db.Friends.Find(x => x.Id == relation.Id).SingleAsync()).Status);
        Assert.Equal(0, await f.Db.Chats.CountDocumentsAsync(x => x.IsGroup)); Assert.Equal(1, await f.Db.Chats.CountDocumentsAsync(_ => true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewChatCannotOutrunParticipantDeletionOrPrivacyChange(bool makePrivate)
    {
        using var f = new UserFactory(); await f.InitializeAsync();
        var snapshotRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var transactionFinds = new ConcurrentDictionary<int, bool>();
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("USER_TEST_MONGO")); settings.DirectConnection = true; settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ClusterConfigurator = cluster =>
        {
            cluster.Subscribe<CommandStartedEvent>(e => { if (e.CommandName == "find" && e.Command["find"].AsString == "users" && e.Command.Contains("txnNumber")) transactionFinds.TryAdd(e.RequestId, true); });
            cluster.Subscribe<CommandSucceededEvent>(e =>
            {
                if (!transactionFinds.TryRemove(e.RequestId, out _) || !e.Reply.TryGetValue("cursor", out var cursorValue)) return;
                var cursor = cursorValue.AsBsonDocument;
                if (cursor["ns"].AsString == f.DatabaseName + ".users" && cursor["firstBatch"].AsBsonArray.Any(value => value.AsBsonDocument["IdentityUserId"].AsString == f.Bob)) snapshotRead.TrySetResult();
            });
        };
        using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IMongoClient>(); services.AddSingleton<IMongoClient>(new MongoClient(settings)); }));
        using var contender = app.CreateClient(); contender.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Token(f.Alice));
        using var held = await f.Services.GetRequiredService<IMongoClient>().StartSessionAsync(); held.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        if (makePrivate) await f.Db.Users.UpdateOneAsync(held, x => x.IdentityUserId == f.Bob, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        else await f.Db.Users.DeleteOneAsync(held, x => x.IdentityUserId == f.Bob);
        var creating = contender.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = new[] { f.Alice, f.Bob, f.Mallory }, isGroup = true });
        await snapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(5)); await held.CommitTransactionAsync();
        Assert.Equal(makePrivate ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, (await creating.WaitAsync(TimeSpan.FromSeconds(5))).StatusCode);
        Assert.Equal(0, await f.Db.Chats.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Messages.CountDocumentsAsync(_ => true));
        if (makePrivate) Assert.True((await f.Db.Users.Find(x => x.IdentityUserId == f.Bob).SingleAsync()).IsPrivate);
        else Assert.Equal(0, await f.Db.Users.CountDocumentsAsync(x => x.IdentityUserId == f.Bob));
    }

    [Fact]
    public async Task NotificationFailureRollsBackBeforeCommitButLiveFailureAcknowledgesPersistedSend()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var original = f.Client(f.Alice); var chat = await Create(f, original); var fault = new NoticeFault();
        using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<INotificationService>(); services.AddScoped<INotificationService>(sp => new FaultyNotifications(new NotificationService(sp.GetRequiredService<User.Data.MongoDbContext>(), sp.GetRequiredService<IHubContext<NotificationHub>>(), sp.GetRequiredService<TimeProvider>()), fault));
        }));
        using var alice = app.CreateClient(); alice.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", f.Token(f.Alice));
        var nonce = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alice.PostAsJsonAsync($"/api/chats/{chat}/messages", new { content = "transactional", clientMessageId = nonce })).StatusCode);
        Assert.Equal(0, await f.Db.Messages.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true)); Assert.Null((await f.Db.Chats.Find(x => x.Id == chat).SingleAsync()).LastMessageId);
        fault.Persist = false; var committed = await Send(alice, chat, "transactional", nonce); var replay = await Send(alice, chat, "transactional", nonce);
        Assert.Equal(committed["id"]!.GetValue<string>(), replay["id"]!.GetValue<string>()); Assert.Equal(1, await f.Db.Messages.CountDocumentsAsync(_ => true)); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true)); Assert.Equal(1, fault.PublishCalls);
        var group = await Create(f, original, f.Alice, f.Bob, f.Mallory); var groupNonce = Guid.NewGuid().ToString();
        await Send(alice, group, "every independent group notice is attempted", groupNonce); await Send(alice, group, "every independent group notice is attempted", groupNonce);
        Assert.Equal(3, fault.PublishCalls); Assert.Equal(2, await f.Db.Messages.CountDocumentsAsync(_ => true)); Assert.Equal(3, await f.Db.Notifications.CountDocumentsAsync(_ => true));
    }

    private sealed class NoticeFault { public bool Persist = true; public int PublishCalls; }
    private sealed class FaultyNotifications(INotificationService inner, NoticeFault fault) : INotificationService
    {
        public Task<Notification> PersistAsync(IClientSessionHandle session, Notification n, CancellationToken ct) => fault.Persist ? throw new IOException("Injected unavailable notification persistence") : inner.PersistAsync(session, n, ct);
        public Task PublishAsync(Notification n) { Interlocked.Increment(ref fault.PublishCalls); throw new IOException("Injected unavailable live notification transport"); }
        public Task RefreshCountAsync(string userId) => inner.RefreshCountAsync(userId);
        public Task<Notification> CreateNotificationAsync(string userId, NotificationType type, string title, string message, string? source = null, Dictionary<string, object>? data = null, string? url = null, string? key = null) => inner.CreateNotificationAsync(userId, type, title, message, source, data, url, key);
        public Task<List<Notification>> GetUserNotificationsAsync(string userId, int limit = 50, int skip = 0) => inner.GetUserNotificationsAsync(userId, limit, skip);
        public Task<int> GetUnreadCountAsync(string userId) => inner.GetUnreadCountAsync(userId);
        public Task MarkAsReadAsync(string id, string userId) => inner.MarkAsReadAsync(id, userId);
        public Task MarkAsHandledAsync(string id, string userId) => inner.MarkAsHandledAsync(id, userId);
        public Task MarkAllAsReadAsync(string userId) => inner.MarkAllAsReadAsync(userId);
        public Task CleanupOldNotificationsAsync(string userId, int days = 30) => inner.CleanupOldNotificationsAsync(userId, days);
    }
}
