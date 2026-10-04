using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using User.Entities;
using User.Services;
using Xunit;

namespace User.Tests;

public sealed class FeedSecurityRegressionTests
{
    private static Task<HttpResponseMessage> GetPost(HttpClient client, string id) => client.GetAsync("/api/feed/post?id=" + Uri.EscapeDataString(id));
    private static Task<HttpResponseMessage> GetReactions(HttpClient client, string id) => client.GetAsync("/api/feed/reactions/by-post?postId=" + Uri.EscapeDataString(id));
    private static Task<HttpResponseMessage> React(HttpClient client, string id) => client.PostAsJsonAsync("/api/feed/reactions", new { postId = id, emoji = "👍", fromIdentityUserId = Guid.NewGuid().ToString(), fromUserName = "forged", toIdentityUserId = Guid.NewGuid().ToString() });
    private static string Week(UserFactory f) => HistoryService.WeekStart(f.Clock.Value.UtcDateTime).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
    private static HistoryEvent Listen(UserFactory f, string actor, string artist, List<string>? names = null) => new() { IdentityUserId = actor, SongId = UserFactory.Song, SongTitle = "Historical song", Artist = artist, ArtistNames = names, PlayedAt = f.Clock.Value.UtcDateTime, Duration = 12 };

    [Fact]
    public async Task PrivateListeningSourcesRemainHiddenFromAcceptedFriendsAndFollowersWhileOwnerAndAdminCanRead()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); f.Clock.Value = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero);
        using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); using var admin = f.Client(f.Mallory, "Admin"); using var anonymous = f.Client();
        var profiles = await f.Db.Users.Find(_ => true).ToListAsync(); var a = profiles.Single(x => x.IdentityUserId == f.Alice); var b = profiles.Single(x => x.IdentityUserId == f.Bob);
        await f.Db.Users.UpdateOneAsync(x => x.Id == b.Id, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        await f.Db.Friends.InsertOneAsync(new Friend { UserId = a.Id, FriendId = b.Id, PairKey = SocialCommands.Pair(a.Id, b.Id), Status = FriendStatus.Accepted });
        await f.Db.Follows.InsertOneAsync(new FollowEdge { FollowerId = f.Alice, FollowedId = f.Bob });
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync($"/api/users/{f.Bob}/listening-history", new { songId = UserFactory.Song, duration = 12 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync("/api/feed/nowplaying", new { songId = UserFactory.Song, isPlaying = true, positionSec = 1 })).StatusCode);
        var post = await f.Db.Feed.Find(x => x.IdentityUserId == f.Bob).SingleAsync();
        var ids = new[] { post.Id, $"weekly:artists:{f.Bob}:{Week(f)}", $"weekly:songs:{f.Bob}:{Week(f)}", $"nowplaying:{f.Bob}:{UserFactory.Song}" };
        foreach (var id in ids)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetPost(anonymous, id)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await GetPost(alice, id)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await GetReactions(alice, id)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetPost(bob, id)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetPost(admin, id)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/feed/slides")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await React(anonymous, post.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await React(alice, post.Id)).StatusCode);
        Assert.Equal(0, await f.Db.Reactions.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true));
        var hidden = await alice.PostAsJsonAsync("/api/feed/nowplaying/batch", new { userIds = new[] { f.Bob } }); Assert.Empty((await hidden.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        var visible = await admin.PostAsJsonAsync("/api/feed/nowplaying/batch", new { userIds = new[] { f.Bob } }); Assert.Single((await visible.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        var slides = await alice.GetFromJsonAsync<JsonElement>("/api/feed/slides?limit=100"); Assert.DoesNotContain(slides.EnumerateArray(), item => item.GetProperty("identityUserId").GetString() == f.Bob);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/feed/reactions/latest?identityUserId={f.Bob}")).StatusCode);
    }

    [Fact]
    public async Task VirtualPostReadAndReactionShareCanonicalSundaySourceAndComparisonAuthorization()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); f.Clock.Value = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero); using var alice = f.Client(f.Alice);
        await f.Db.History.InsertManyAsync(new[] { Listen(f, f.Alice, "Shared artist"), Listen(f, f.Bob, "Shared artist") });
        var week = HistoryService.WeekStart(f.Clock.Value.UtcDateTime);
        var invalid = new (string Id, HttpStatusCode Status)[]
        {
            ($"weekly:artists:{f.Bob}:{week.AddDays(-6):yyyyMMdd}", HttpStatusCode.BadRequest),
            ($"weekly:songs:{f.Bob}:{week.AddDays(7):yyyyMMdd}", HttpStatusCode.BadRequest),
            ($"weekly:artists:{f.Bob}:{week.AddDays(-91):yyyyMMdd}", HttpStatusCode.BadRequest),
            ($"weekly:artists:{f.Mallory}:{Week(f)}", HttpStatusCode.NotFound),
            ($"weekly:albums:{f.Bob}:{Week(f)}", HttpStatusCode.NotFound),
            ($"common:{f.Mallory}:{f.Bob}:{Week(f)}", HttpStatusCode.Forbidden),
            ($"common:{f.Alice}:{f.Mallory}:{Week(f)}", HttpStatusCode.NotFound)
        };
        foreach (var (id, status) in invalid)
        {
            Assert.Equal(status, (await GetPost(alice, id)).StatusCode); Assert.Equal(status, (await React(alice, id)).StatusCode);
        }
        Assert.Equal(0, await f.Db.Reactions.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Notifications.CountDocumentsAsync(_ => true));
        foreach (var id in new[] { $"weekly:artists:{f.Bob}:{Week(f)}", $"weekly:songs:{f.Bob}:{Week(f)}", $"common:{f.Alice}:{f.Bob}:{Week(f)}" })
        {
            var response = await GetPost(alice, id); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(id, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("postId").GetString());
            Assert.Equal(HttpStatusCode.OK, (await React(alice, id)).StatusCode); var row = await f.Db.Reactions.Find(x => x.PostId == id).SingleAsync(); Assert.Equal(f.Alice, row.FromIdentityUserId); Assert.Equal(f.Bob, row.ToIdentityUserId); Assert.Equal("alice", row.FromUserName);
        }
    }

    [Fact]
    public async Task SharedComparisonValidatorPreservesCommaContainingCanonicalArtistNamesAndLegacyFallback()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); f.Clock.Value = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero); using var alice = f.Client(f.Alice);
        await f.Db.History.InsertManyAsync(new[] { Listen(f, f.Bob, "Earth, Wind & Fire", ["Earth, Wind & Fire"]), Listen(f, f.Alice, "Wind", ["Wind"]) });
        var id = $"common:{f.Alice}:{f.Bob}:{Week(f)}";
        Assert.Equal(HttpStatusCode.NotFound, (await GetPost(alice, id)).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await React(alice, id)).StatusCode); Assert.Equal(0, await f.Db.Reactions.CountDocumentsAsync(_ => true));
        await f.Db.History.InsertOneAsync(Listen(f, f.Alice, "Earth, Wind & Fire", ["Earth, Wind & Fire"]));
        var actual = await GetPost(alice, id); Assert.Equal(HttpStatusCode.OK, actual.StatusCode); var comparison = await actual.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("Earth, Wind & Fire", Assert.Single(comparison.GetProperty("commonArtists").EnumerateArray()).GetString());
        Assert.Equal(HttpStatusCode.OK, (await React(alice, id)).StatusCode);
        await f.Db.History.InsertManyAsync(new[] { Listen(f, f.Alice, "Legacy one, Legacy two"), Listen(f, f.Mallory, "Legacy one") });
        var legacy = $"common:{f.Alice}:{f.Mallory}:{Week(f)}"; Assert.Equal(HttpStatusCode.OK, (await GetPost(alice, legacy)).StatusCode); Assert.Equal(HttpStatusCode.OK, (await React(alice, legacy)).StatusCode);
    }

    [Fact]
    public async Task CanonicalObjectIdAliasesReadSamePersistedReactionAndDeletedPostCannotAcquireNewReaction()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice);
        var post = new FeedItem { Id = "abcdefabcdefabcdefabcdef", Key = "canonical-case", IdentityUserId = f.Bob, Type = "recent_song", SongId = UserFactory.Song, SongTitle = "Historical song", Artist = "Artist", PlayedAt = f.Clock.Value.UtcDateTime };
        await f.Db.Feed.InsertOneAsync(post); Assert.Equal(HttpStatusCode.OK, (await React(alice, post.Id.ToUpperInvariant())).StatusCode);
        var row = await f.Db.Reactions.Find(_ => true).SingleAsync(); Assert.Equal(post.Id, row.PostId); Assert.Equal(f.Alice, row.FromIdentityUserId); Assert.Equal(f.Bob, row.ToIdentityUserId);
        foreach (var id in new[] { post.Id, post.Id.ToUpperInvariant() })
        {
            var response = await GetReactions(alice, id); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(row.Id, Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()).GetProperty("id").GetString());
        }
        await f.Db.Feed.DeleteOneAsync(x => x.Id == post.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await GetPost(alice, post.Id)).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await GetReactions(alice, post.Id)).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await React(alice, post.Id)).StatusCode);
        Assert.Equal(1, await f.Db.Reactions.CountDocumentsAsync(_ => true)); Assert.Equal(1, await f.Db.Notifications.CountDocumentsAsync(_ => true));
    }
}
