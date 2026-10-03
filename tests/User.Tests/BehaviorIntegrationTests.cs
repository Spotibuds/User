using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using User.Entities;
using User.Services;
using Xunit;

namespace User.Tests;

public class BehaviorIntegrationTests
{
    private static async Task<JsonObject> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    private static async Task<string> Chat(UserFactory factory, HttpClient alice)
    {
        using var created = await alice.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = new[] { factory.Alice, factory.Bob } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode); return (await Json(created))["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task ActualJwtMiddlewareRejectsBadSignatureIssuerAudienceExpiredAndMissingSid()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync();
        foreach (var token in new[] { factory.Token(factory.Alice, signingSecret: "incorrect-signing-secret-at-least-32-bytes"), factory.Token(factory.Alice, issuer: "bad"),
            factory.Token(factory.Alice, audience: "bad"), factory.Token(factory.Alice, expires: DateTime.UtcNow.AddMinutes(-1)), factory.Token(factory.Alice, includeSid: false) })
        {
            using var client = factory.Client(token: token); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/users/check/{factory.Alice}")).StatusCode);
        }
        using var good = factory.Client(factory.Alice); Assert.Equal(HttpStatusCode.OK, (await good.GetAsync($"/api/users/check/{factory.Alice}")).StatusCode);
        factory.Dependencies.SessionStatus = HttpStatusCode.Unauthorized;
        Assert.Equal(HttpStatusCode.Unauthorized, (await good.GetAsync($"/api/users/check/{factory.Alice}")).StatusCode);
        factory.Dependencies.SessionStatus = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await good.GetAsync($"/api/users/check/{factory.Alice}")).StatusCode);
    }

    [Fact]
    public async Task AnonymousAndForeignProfileHistoryWritesLeavePersistedStateUntouched()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var anonymous = factory.Client(); using var alice = factory.Client(factory.Alice);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"/api/users/{factory.Bob}", new { bio = "forged" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync($"/api/users/{factory.Bob}", new { bio = "forged" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync($"/api/users/{factory.Bob}/listening-history", new { songId = UserFactory.Song, songTitle = "Demo", artist = "Artist", duration = 12 })).StatusCode);
        Assert.Equal(0, await factory.Db.History.CountDocumentsAsync(_ => true));
        Assert.Null((await factory.Db.Users.Find(x => x.IdentityUserId == factory.Bob).SingleAsync()).Bio);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsJsonAsync($"/api/users/{factory.Alice}", new { displayName = "", bio = (string?)null })).StatusCode);
        var stored = await factory.Db.Users.Find(x => x.IdentityUserId == factory.Alice).SingleAsync(); Assert.Equal("", stored.DisplayName); Assert.Equal("", stored.Bio);
        var bobProfile = await factory.Db.Users.Find(x => x.IdentityUserId == factory.Bob).SingleAsync();
        await factory.Db.Friends.InsertOneAsync(new Friend { UserId = stored.Id, FriendId = bobProfile.Id, PairKey = SocialCommands.Pair(stored.Id, bobProfile.Id) });
        // The removed literal reset route is rejected by the remaining validated {id} route.
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.DeleteAsync("/api/friends/reset")).StatusCode);
        Assert.Equal(1, await factory.Db.Friends.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task PrivateDirectAndAggregateReadsAreDeniedOrRedacted()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync();
        await factory.Db.Users.UpdateOneAsync(x => x.IdentityUserId == factory.Bob, Builders<Models.User>.Update.Set(x => x.IsPrivate, true).Set(x => x.Bio, "private biography"));
        using var anonymous = factory.Client(); using var mallory = factory.Client(factory.Mallory); using var bob = factory.Client(factory.Bob); using var admin = factory.Client(factory.Mallory, "Admin");
        foreach (var path in new[] { $"/api/users/{factory.Bob}", $"/api/users/{factory.Bob}/listening-history", $"/api/users/identity/{factory.Bob}/top-artists/week/current" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(path)).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync(path)).StatusCode); Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path)).StatusCode);
        }
        using var batch = await mallory.PostAsJsonAsync("/api/users/batch", new { userIds = new[] { factory.Bob } });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode); var users = (await batch.Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.Null(users[0]!["bio"]); Assert.Null(users[0]!["displayName"]);
    }

    [Fact]
    public async Task ForgedFollowActorIsIgnoredAndConcurrentEdgesStayUnique()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice); using var anonymous = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/follows", new { followerId = factory.Bob, followedId = factory.Mallory })).StatusCode);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => alice.PostAsJsonAsync("/api/follows", new { followerId = factory.Bob, followedId = factory.Mallory })));
        Assert.All(results, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(1, await factory.Db.Follows.CountDocumentsAsync(x => x.FollowerId == factory.Alice && x.FollowedId == factory.Mallory));
        Assert.Equal(0, await factory.Db.Follows.CountDocumentsAsync(x => x.FollowerId == factory.Bob));
    }

    [Fact]
    public async Task NotificationRecipientAuthorizationAndTerminalFriendRequestStatePersist()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice); using var bob = factory.Client(factory.Bob); using var mallory = factory.Client(factory.Mallory);
        var sent = await Json(await alice.PostAsJsonAsync("/api/friends/request", new { targetUserId = factory.Bob })); var friendship = sent["friendshipId"]!.GetValue<string>();
        var notice = await factory.Db.Notifications.Find(x => x.TargetUserId == factory.Bob && x.Type == NotificationType.FriendRequest).SingleAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync($"/api/notifications/{factory.Bob}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.PostAsync($"/api/notifications/{notice.Id}/read", null)).StatusCode);
        Assert.Equal(NotificationStatus.Unread, (await factory.Db.Notifications.Find(x => x.Id == notice.Id).SingleAsync()).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync($"/api/friends/{friendship}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/friends/{friendship}/accept", null)).StatusCode);
        Assert.Equal(NotificationStatus.Handled, (await factory.Db.Notifications.Find(x => x.Id == notice.Id).SingleAsync()).Status);
        var reloaded = await Json(await bob.GetAsync($"/api/notifications/{factory.Bob}"));
        Assert.Equal(0, reloaded["unreadCount"]!.GetValue<int>());
        Assert.Contains(reloaded["notifications"]!.AsArray(), n => n!["id"]!.GetValue<string>() == notice.Id && n["type"]!.GetValue<string>() == "FriendRequest" && n["status"]!.GetValue<string>() == "Handled");
        Assert.Equal(1, await factory.Db.Notifications.CountDocumentsAsync(x => x.TargetUserId == factory.Alice && x.Type == NotificationType.FriendRequestAccepted));
    }

    [Fact]
    public async Task ThirdPartyChatCreationAndNonmemberReadsAreForbiddenWithoutSideEffects()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice); using var mallory = factory.Client(factory.Mallory);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = new[] { factory.Alice, factory.Bob } })).StatusCode);
        Assert.Equal(0, await factory.Db.Chats.CountDocumentsAsync(_ => true));
        var id = await Chat(factory, alice);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync($"/api/chats/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.PostAsJsonAsync($"/api/chats/{id}/messages", new { content = "forged" })).StatusCode);
        Assert.Equal(0, await factory.Db.Messages.CountDocumentsAsync(_ => true));
        var repeated = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => alice.PostAsJsonAsync("/api/chats/create-or-get", new { participantIds = new[] { factory.Alice, factory.Bob } })));
        Assert.All(repeated, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode)); Assert.Equal(1, await factory.Db.Chats.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task RestAndHubMessagesShareCanonicalIdsDeliveryAndIdempotentReceipts()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice); using var bob = factory.Client(factory.Bob); using var mallory = factory.Client(factory.Mallory);
        var chat = await Chat(factory, alice); await using var bobHub = factory.Hub("/chat-hub", factory.Bob); await using var aliceHub = factory.Hub("/chat-hub", factory.Alice); await using var malloryHub = factory.Hub("/chat-hub", factory.Mallory);
        var delivered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobHub.On<JsonElement>("ReceiveMessage", dto => delivered.TrySetResult(dto));
        await bobHub.StartAsync(); await bobHub.InvokeAsync("JoinChat", chat); await aliceHub.StartAsync(); await malloryHub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => malloryHub.InvokeAsync("JoinChat", chat));
        var clientId = Guid.NewGuid().ToString();
        var dto = await Json(await alice.PostAsJsonAsync($"/api/chats/{chat}/messages", new { content = "canonical REST message", clientMessageId = clientId }));
        Assert.False(dto["isRead"]!.GetValue<bool>());
        var live = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(dto["id"]!.GetValue<string>(), live.GetProperty("id").GetString());
        var duplicate = await aliceHub.InvokeAsync<JsonElement>("SendMessage", chat, "canonical REST message", clientId);
        Assert.Equal(dto["id"]!.GetValue<string>(), duplicate.GetProperty("id").GetString()); Assert.Equal(1, await factory.Db.Messages.CountDocumentsAsync(_ => true));
        var messageId = dto["id"]!.GetValue<string>();
        await aliceHub.InvokeAsync("MarkAsRead", messageId);
        var senderOnly = (await (await alice.GetAsync($"/api/chats/{chat}/messages")).Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.False(senderOnly.Single()!["isRead"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.PostAsync($"/api/chats/messages/{messageId}/read", null)).StatusCode);
        await Assert.ThrowsAsync<HubException>(() => malloryHub.InvokeAsync("MarkMessageAsRead", messageId));
        await Assert.ThrowsAsync<HubException>(() => malloryHub.InvokeAsync("MarkAllMessagesAsRead", chat));
        await bobHub.InvokeAsync("MarkMessageAsRead", messageId);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => bob.PostAsync($"/api/chats/messages/{messageId}/read", null)));
        var stored = await factory.Db.Messages.Find(x => x.Id == messageId).SingleAsync(); Assert.Equal(1, stored.ReadBy.Count(r => r.UserId == factory.Bob));
        var read = (await (await alice.GetAsync($"/api/chats/{chat}/messages")).Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.True(read.Single()!["isRead"]!.GetValue<bool>());
        Assert.Equal(messageId, (await factory.Db.Chats.Find(x => x.Id == chat).SingleAsync()).LastMessageId);
        var second = await Json(await alice.PostAsJsonAsync($"/api/chats/{chat}/messages", new { content = "second unread message", clientMessageId = Guid.NewGuid().ToString() }));
        Assert.False(second["isRead"]!.GetValue<bool>());
        await bobHub.InvokeAsync("MarkAllMessagesAsRead", chat);
        await bobHub.InvokeAsync("MarkAllMessagesAsRead", chat);
        var allMessages = await factory.Db.Messages.Find(x => x.ChatId == chat).ToListAsync();
        Assert.Equal(2, allMessages.Count); Assert.All(allMessages, message => Assert.Equal(1, message.ReadBy.Count(reader => reader.UserId == factory.Bob)));
        var allRead = (await (await alice.GetAsync($"/api/chats/{chat}/messages")).Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.All(allRead, message => Assert.True(message!["isRead"]!.GetValue<bool>()));
        factory.Dependencies.SessionStatus = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAnyAsync<Exception>(() => aliceHub.InvokeAsync("StartTyping", chat));
    }

    [Fact]
    public async Task NotificationHubDoesNotExposeServerOnlyDeliveryHelper()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); await using var hub = factory.Hub("/notification-hub", factory.Mallory);
        await hub.StartAsync(); await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("SendNotificationToUser", factory.Bob, new { title = "forged" }));
        Assert.Equal(0, await factory.Db.Notifications.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task FeedPlaybackAndReactionActorFieldsComeFromClaims()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice); using var anonymous = factory.Client();
        var state = new { identityUserId = factory.Bob, songId = UserFactory.Song, songTitle = "Demo", artist = "Artist", positionSec = 0, isPlaying = true };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/feed/nowplaying", state)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/feed/nowplaying", state)).StatusCode);
        var playing = factory.Services.GetRequiredService<INowPlayingStore>(); Assert.NotNull(playing.Get(factory.Alice)); Assert.Null(playing.Get(factory.Bob));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/feed/nowplaying/{factory.Bob}")).StatusCode);
        var post = new FeedItem { IdentityUserId = factory.Alice, Key = "recent_song:" + factory.Alice + ":" + UserFactory.Song, Type = "recent_song", SongId = UserFactory.Song, SongTitle = "Demo", Artist = "Artist" }; await factory.Db.Feed.InsertOneAsync(post);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/feed/reactions", new { postId = post.Id, contextType = "recent_song", songId = UserFactory.Song, toIdentityUserId = factory.Alice, fromIdentityUserId = factory.Bob, fromUserName = "forged", emoji = "❤️" })).StatusCode);
        var reaction = await factory.Db.Reactions.Find(_ => true).SingleAsync(); Assert.Equal(factory.Alice, reaction.FromIdentityUserId); Assert.Equal("alice", reaction.FromUserName); Assert.Equal(post.Id, reaction.PostId);
    }

    [Fact]
    public async Task HistoryPaginationRetainsMoreThanOneHundredEventsAndWeekRankingUpdates()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice);
        factory.Clock.Value = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 105; index++)
        {
            factory.Dependencies.Artist = index < 101 ? "Artist A" : "Artist B";
            using var response = await alice.PostAsJsonAsync($"/api/users/{factory.Alice}/listening-history", new { songId = UserFactory.Song, songTitle = "Forged title", artist = "Forged artist", duration = 12 }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var ranking = (await (await alice.GetAsync($"/api/users/identity/{factory.Alice}/top-artists/week/current")).Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.Equal(101, ranking[0]!["count"]!.GetValue<int>()); Assert.Equal(4, ranking[1]!["count"]!.GetValue<int>());
        var tail = (await (await alice.GetAsync($"/api/users/{factory.Alice}/listening-history?limit=10&skip=100")).Content.ReadFromJsonAsync<JsonArray>())!; Assert.Equal(5, tail.Count);
        Assert.Equal(105, await factory.Db.History.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await factory.Db.History.CountDocumentsAsync(x => x.Artist == "Forged artist" || x.SongTitle != "Test song"));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/users/{factory.Alice}/listening-history?limit=0")).StatusCode);
        var post = await factory.Db.Feed.Find(_ => true).SingleAsync(); Assert.True(MongoDB.Bson.ObjectId.TryParse(post.Id, out _)); Assert.Equal("Test song", post.SongTitle); Assert.Equal("Artist B", post.Artist);
    }

    [Fact]
    public async Task PresenceAndActiveChatKeepRemainingTabAndWeekWindowIsDeterministic()
    {
        var presence = new PresenceStore(); Assert.True(presence.Add("user", "tab1")); Assert.False(presence.Add("user", "tab2"));
        Assert.False(presence.Remove("user", "tab1")); Assert.True(presence.Online("user")); Assert.True(presence.Remove("user", "tab2")); Assert.False(presence.Online("user"));
        var active = new ActiveChatTrackingService(); active.AddUserToChat("chat", "user", "tab1"); active.AddUserToChat("chat", "user", "tab2"); active.RemoveUserFromAllChats("user", "tab1"); Assert.True(active.IsUserInChat("chat", "user")); active.RemoveUserFromAllChats("user", "tab2"); Assert.False(active.IsUserInChat("chat", "user"));
        Assert.Equal(new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc), HistoryService.WeekStart(new DateTime(2026, 10, 3, 23, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc), HistoryService.WeekStart(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)));
        await Task.CompletedTask;
    }
}
