using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using User.Data;
using User.Entities;
using User.Services;
using Xunit;

namespace User.Tests;

public sealed class FeedRegressionTests
{
    private static HistoryEvent Listen(UserFactory f, string actor, string title = "Snapshot", string artist = "Shared artist") => new() { IdentityUserId = actor, SongId = UserFactory.Song, SongTitle = title, Artist = artist, PlayedAt = f.Clock.Value.UtcDateTime, Duration = 12 };
    private static FeedItem Post(UserFactory f, string actor, string song) => new() { IdentityUserId = actor, Type = "recent_song", SongId = song, SongTitle = "Persisted snapshot", Artist = "Shared artist", PlayedAt = f.Clock.Value.UtcDateTime, Key = $"recent_song:{actor}:{song}" };
    private static async Task<List<JsonElement>> AllPages(HttpClient client, int limit = 2)
    {
        var result = new List<JsonElement>(); string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            var response = await client.GetAsync($"/api/feed/slides/page?limit={limit}" + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            result.AddRange(data.GetProperty("items").EnumerateArray().Select(x => x.Clone()));
            if (!data.GetProperty("hasMore").GetBoolean()) { Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind); return result; }
            var next = data.GetProperty("nextCursor").GetString(); Assert.False(string.IsNullOrEmpty(next)); Assert.NotEqual(cursor, next); cursor = next;
        }
        throw new Xunit.Sdk.XunitException("Feed continuation did not terminate within the fixture's bounded pages.");
    }
    [Fact]
    public async Task ItemContinuationRetainsEveryTypeAndStableTieOrderWithoutAuthorApproximation()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); f.Clock.Value = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero);
        await f.Db.History.InsertManyAsync(new[] { Listen(f, f.Alice), Listen(f, f.Bob), Listen(f, f.Mallory) });
        var posts = new[] { Post(f, f.Bob, UserFactory.Song), Post(f, f.Bob, "222222222222222222222222"), Post(f, f.Mallory, UserFactory.Song) };
        await f.Db.Feed.InsertManyAsync(posts);
        var store = f.Services.GetRequiredService<INowPlayingStore>();
        foreach (var actor in new[] { f.Bob, f.Mallory }) store.Set(new NowPlayingState { IdentityUserId = actor, SongId = UserFactory.Song, IsPlaying = true, SongTitle = "Live" }, TimeSpan.FromSeconds(180));
        using var alice = f.Client(f.Alice); var result = await AllPages(alice);
        Assert.Equal(11, result.Count); Assert.Equal(result.Count, result.Select(x => x.GetProperty("postId").GetString()).Distinct().Count());
        Assert.Equal(new[] { "now_playing", "recent_song", "top_artists_week", "top_songs_week", "common_artists" }.Order(), result.Select(x => x.GetProperty("type").GetString()!).Distinct().Order());
        Assert.Equal(posts.OrderByDescending(x => x.Id).Select(x => x.Id), result.Where(x => x.GetProperty("type").GetString() == "recent_song").Select(x => x.GetProperty("postId").GetString()));
        var old = await alice.GetFromJsonAsync<JsonElement>("/api/feed/slides?limit=4&skip=3"); Assert.Equal(result.Skip(3).Take(4).Select(x => x.GetProperty("postId").GetString()), old.EnumerateArray().Select(x => x.GetProperty("postId").GetString()));
    }
    [Fact]
    public async Task EmptyAuthorWindowsAdvanceWithoutFalseExhaustionAndNeverIncludeSelfOrPrivateAuthors()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); f.Clock.Value = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero);
        var empty = Enumerable.Range(0, 450).Select(i => new Models.User { Id = ObjectId.GenerateNewId().ToString(), IdentityUserId = Guid.NewGuid().ToString(), UserName = "empty" + i }).ToList();
        await f.Db.Users.InsertManyAsync(empty); var author = empty[^1];
        await f.Db.History.InsertManyAsync(new[] { Listen(f, author.IdentityUserId), Listen(f, f.Alice), Listen(f, f.Bob) });
        await f.Db.Users.UpdateOneAsync(x => x.IdentityUserId == f.Bob, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        using var alice = f.Client(f.Alice); var first = await alice.GetFromJsonAsync<JsonElement>("/api/feed/slides/page?limit=3");
        Assert.Empty(first.GetProperty("items").EnumerateArray()); Assert.True(first.GetProperty("hasMore").GetBoolean());
        var result = await AllPages(alice, 3); Assert.Equal(3, result.Count); Assert.All(result, x => Assert.Equal(author.IdentityUserId, x.GetProperty("identityUserId").GetString()));
    }
    [Fact]
    public async Task CursorBindsOwnerBoundaryAndExpiryWhileDependencyErrorsDoNotMutateFeed()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob); using var anonymous = f.Client();
        await f.Db.History.InsertOneAsync(Listen(f, f.Bob)); var first = await alice.GetFromJsonAsync<JsonElement>("/api/feed/slides/page?limit=1"); var cursor = first.GetProperty("nextCursor").GetString()!;
        Assert.Equal(HttpStatusCode.Gone, (await bob.GetAsync("/api/feed/slides/page?cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await alice.GetAsync("/api/feed/slides/page?cursor=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/feed/slides/page?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/feed/slides/page")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/feed/nowplaying/batch", new { userIds = (string[]?)null })).StatusCode);
        f.Clock.Value = f.Clock.Value.AddMinutes(11); Assert.Equal(HttpStatusCode.Gone, (await alice.GetAsync("/api/feed/slides/page?cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        f.Dependencies.SessionStatus = HttpStatusCode.ServiceUnavailable; Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alice.GetAsync("/api/feed/slides/page")).StatusCode);
        Assert.Equal(1, await f.Db.History.CountDocumentsAsync(_ => true)); Assert.Equal(0, await f.Db.Feed.CountDocumentsAsync(_ => true));
    }
    [Fact]
    public async Task OnePageSnapshotCannotMixOldPublicProfileWithNewPrivateHistoryOrPlayback()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); f.Clock.Value = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero); await f.Db.History.InsertOneAsync(Listen(f, f.Bob, "Public snapshot"));
        var gate = new PublicSelectionGate(); var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("USER_TEST_MONGO")); settings.DirectConnection = true;
        settings.ClusterConfigurator = cluster => { cluster.Subscribe<CommandStartedEvent>(gate.Started); cluster.Subscribe<CommandSucceededEvent>(gate.Succeeded); };
        using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IMongoClient>(); services.AddSingleton<IMongoClient>(new MongoClient(settings)); }));
        using var alice = app.CreateClient(); alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Alice)); gate.Enabled = true;
        var pending = alice.GetAsync("/api/feed/slides/page?limit=100"); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Db.Users.UpdateOneAsync(x => x.IdentityUserId == f.Bob, Builders<Models.User>.Update.Set(x => x.IsPrivate, true)); // Keep the clock fixed: authorization must not depend on timestamp precision.
        await f.Db.History.InsertOneAsync(Listen(f, f.Bob, "New private source", "Private artist")); await f.Db.Feed.InsertOneAsync(Post(f, f.Bob, UserFactory.Song));
        app.Services.GetRequiredService<INowPlayingStore>().Set(new NowPlayingState { IdentityUserId = f.Bob, SongId = UserFactory.Song, IsPlaying = true, SongTitle = "New private live" }, TimeSpan.FromSeconds(90)); gate.Release.TrySetResult();
        var response = await pending; Assert.Equal(HttpStatusCode.OK, response.StatusCode); var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("Private artist", text); Assert.DoesNotContain("New private", text);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/feed/post?id=weekly:artists:{f.Bob}:20300407")).StatusCode);
    }
    [Fact]
    public async Task CanonicalArtistArrayAndSongIdentityPreserveActualNamesAndLatestHistoricalMetadata()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); f.Clock.Value = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero); f.Dependencies.Artist = "Earth, Wind & Fire"; using var bob = f.Client(f.Bob);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync($"/api/users/{f.Bob}/listening-history", new { songId = UserFactory.Song, duration = 12 })).StatusCode);
        var row = await f.Db.History.Find(_ => true).SingleAsync(); Assert.Equal(new[] { "Earth, Wind & Fire" }, row.ArtistNames);
        var old = Listen(f, f.Bob, "Older title", "Older artist"); old.PlayedAt = old.PlayedAt.AddSeconds(-1); await f.Db.History.InsertOneAsync(old);
        using var alice = f.Client(f.Alice); var artists = await alice.GetFromJsonAsync<JsonElement>($"/api/feed/post?id=weekly:artists:{f.Bob}:20300407");
        Assert.Contains(artists.GetProperty("topArtists").EnumerateArray(), x => x.GetProperty("name").GetString() == "Earth, Wind & Fire"); Assert.DoesNotContain(artists.GetProperty("topArtists").EnumerateArray(), x => x.GetProperty("name").GetString() == "Earth");
        var songs = await alice.GetFromJsonAsync<JsonElement>($"/api/feed/post?id=weekly:songs:{f.Bob}:20300407"); var song = Assert.Single(songs.GetProperty("topSongs").EnumerateArray()); Assert.Equal(2, song.GetProperty("count").GetInt32()); Assert.Equal("Test song", song.GetProperty("songTitle").GetString());
        var history = await bob.GetFromJsonAsync<JsonElement>($"/api/users/{f.Bob}/listening-history"); Assert.Equal(new[] { "Test song", "Older title" }, history.EnumerateArray().Select(x => x.GetProperty("songTitle").GetString()));
    }
    [Fact]
    public async Task ReactionSummaryIncludesMyStateBeyondLegacyCapAndPeopleCursorReturnsAllTiedRows()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var post = Post(f, f.Bob, UserFactory.Song); await f.Db.Feed.InsertOneAsync(post);
        var reactions = Enumerable.Range(0, 151).Select(i => new Reaction { PostId = post.Id, FromIdentityUserId = i == 0 ? f.Alice : Guid.NewGuid().ToString(), FromUserName = "reactor" + i, ToIdentityUserId = f.Bob, Emoji = i == 0 ? "👍" : "❤️", CreatedAt = f.Clock.Value.UtcDateTime }).ToList(); await f.Db.Reactions.InsertManyAsync(reactions);
        using var alice = f.Client(f.Alice); var summary = await alice.GetFromJsonAsync<JsonElement>("/api/feed/reactions/summary?postId=" + post.Id.ToUpperInvariant()); Assert.Equal(151, summary.GetProperty("total").GetInt32()); Assert.Equal(post.Id, summary.GetProperty("postId").GetString()); Assert.Equal("👍", Assert.Single(summary.GetProperty("myEmojis").EnumerateArray()).GetString());
        var legacy = await alice.GetFromJsonAsync<JsonElement>("/api/feed/reactions/by-post?postId=" + post.Id); Assert.Equal(100, legacy.GetArrayLength()); Assert.DoesNotContain(legacy.EnumerateArray(), x => x.GetProperty("fromIdentityUserId").GetString() == f.Alice);
        var all = new List<string>(); string? cursor = null;
        do { var page = await alice.GetFromJsonAsync<JsonElement>("/api/feed/reactions/people?postId=" + post.Id + "&limit=17" + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))); all.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()!)); Assert.Equal(151, page.GetProperty("total").GetInt32()); cursor = page.GetProperty("nextCursor").GetString(); Assert.Equal(cursor != null, page.GetProperty("hasMore").GetBoolean()); } while (cursor != null);
        Assert.Equal(reactions.OrderByDescending(x => x.Id).Select(x => x.Id), all); Assert.Equal(151, all.Distinct().Count());
        var ack = await alice.PostAsJsonAsync("/api/feed/reactions", new { postId = post.Id.ToUpperInvariant(), emoji = "👍" }); Assert.Equal(HttpStatusCode.OK, ack.StatusCode); var data = await ack.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("removed", data.GetProperty("action").GetString()); Assert.Equal(post.Id, data.GetProperty("postId").GetString());
    }
    [Fact]
    public async Task DelayedPlaybackCannotResurrectAfterPauseAndExpiryUsesControlledClock()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var gate = new CatalogueGate();
        using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddHttpClient("Music").ConfigurePrimaryHttpMessageHandler(() => gate)));
        using var alice = app.CreateClient(); alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Alice));
        var pending = alice.PostAsJsonAsync("/api/feed/nowplaying", new { songId = UserFactory.Song, isPlaying = true, positionSec = 1 }); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/feed/nowplaying/{f.Alice}")).StatusCode); gate.Release.TrySetResult(); Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
        var store = app.Services.GetRequiredService<INowPlayingStore>(); Assert.Null(store.Get(f.Alice));
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/feed/nowplaying?ttlSec=30", new { songId = UserFactory.Song, isPlaying = true, positionSec = 1 })).StatusCode); Assert.NotNull(store.Get(f.Alice)); f.Clock.Value = f.Clock.Value.AddSeconds(31); Assert.Null(store.Get(f.Alice));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackReservationPrecedesProfileLookupAndDelayedPauseCannotClearNewerPlayback(bool delayedPlaying)
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var gate = new ProfileLookupGate(f.Alice); using var app = GatedApp(f, gate);
        using var alice = app.CreateClient(); alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Alice)); gate.Enabled = true;
        var pending = alice.PostAsJsonAsync("/api/feed/nowplaying", new { songId = UserFactory.Song, isPlaying = delayedPlaying, positionSec = 1 }); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var latest = delayedPlaying ? await alice.DeleteAsync($"/api/feed/nowplaying/{f.Alice}") : await alice.PostAsJsonAsync("/api/feed/nowplaying", new { songId = UserFactory.Song, isPlaying = true, positionSec = 2 });
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode); gate.Release.TrySetResult(); Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
        var state = app.Services.GetRequiredService<INowPlayingStore>().Get(f.Alice);
        if (delayedPlaying) Assert.Null(state); else { Assert.NotNull(state); Assert.True(state.IsPlaying); Assert.Equal(2, state.PositionSec); }
    }
    [Fact]
    public async Task LiveDetailCannotExposePlaybackPublishedAfterItsPublicProfileObservation()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var gate = new ProfileLookupGate(f.Bob); using var app = GatedApp(f, gate);
        var store = app.Services.GetRequiredService<INowPlayingStore>(); store.Set(new NowPlayingState { IdentityUserId = f.Bob, SongId = UserFactory.Song, IsPlaying = true, SongTitle = "Earlier public" }, TimeSpan.FromSeconds(90));
        using var alice = app.CreateClient(); alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Alice)); gate.Enabled = true;
        var pending = alice.GetAsync($"/api/feed/post?id=nowplaying:{f.Bob}:{UserFactory.Song}"); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Db.Users.UpdateOneAsync(x => x.IdentityUserId == f.Bob, Builders<Models.User>.Update.Set(x => x.IsPrivate, true)); // Same-time publication is still newer than this observation.
        store.Set(new NowPlayingState { IdentityUserId = f.Bob, SongId = UserFactory.Song, IsPlaying = true, SongTitle = "New private playback" }, TimeSpan.FromSeconds(90)); gate.Release.TrySetResult();
        var response = await pending; Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.DoesNotContain("New private playback", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task PersistedPostIdentityOverridesClientContextAndLiveReactionAcknowledgesCanonicalAlias()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); using var alice = f.Client(f.Alice); using var bob = f.Client(f.Bob);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync("/api/feed/nowplaying", new { songId = UserFactory.Song, isPlaying = true, positionSec = 1 })).StatusCode);
        var live = $"nowplaying:{f.Bob}:{UserFactory.Song}";
        var first = await alice.PostAsJsonAsync("/api/feed/reactions", new { postId = live, contextType = "now_playing", emoji = "👍" }); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var canonical = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("postId").GetString(); Assert.True(ObjectId.TryParse(canonical, out _));
        var second = await alice.PostAsJsonAsync("/api/feed/reactions", new { postId = canonical, contextType = "now_playing", toIdentityUserId = f.Mallory, emoji = "❤️" }); Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(canonical, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("postId").GetString());
        var rows = await f.Db.Reactions.Find(_ => true).ToListAsync(); Assert.Equal(2, rows.Count); Assert.All(rows, row => { Assert.Equal(canonical, row.PostId); Assert.Equal(f.Bob, row.ToIdentityUserId); Assert.Equal("recent_song", row.ContextType); });
        await bob.DeleteAsync($"/api/feed/nowplaying/{f.Bob}"); Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/feed/reactions/summary?postId=" + canonical)).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/feed/reactions/summary?postId=" + Uri.EscapeDataString(live))).StatusCode);
    }
    [Fact]
    public async Task PlaybackBatchCannotMixEarlierPublicProfileWithNewPrivateCacheState()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var gate = new ProfileLookupGate(f.Bob); using var app = GatedApp(f, gate);
        using var alice = app.CreateClient(); alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Alice)); gate.Enabled = true;
        var pending = alice.PostAsJsonAsync("/api/feed/nowplaying/batch", new { userIds = new[] { f.Bob } }); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Db.Users.UpdateOneAsync(x => x.IdentityUserId == f.Bob, Builders<Models.User>.Update.Set(x => x.IsPrivate, true)); // Same-time publication is still newer than this observation.
        app.Services.GetRequiredService<INowPlayingStore>().Set(new NowPlayingState { IdentityUserId = f.Bob, SongId = UserFactory.Song, IsPlaying = true, SongTitle = "New private batch source" }, TimeSpan.FromSeconds(90)); gate.Release.TrySetResult();
        var response = await pending; Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReservedPlaybackPublicationAfterAnObservationIsHiddenEvenWhenClockDoesNotAdvance(int seconds)
    {
        using var f = new UserFactory(); await f.InitializeAsync(); var store = f.Services.GetRequiredService<INowPlayingStore>();
        store.Set(new NowPlayingState { IdentityUserId = f.Bob, SongId = UserFactory.Song, IsPlaying = true, SongTitle = "Visible before observation" }, TimeSpan.FromSeconds(90));
        var pending = store.Reserve(f.Bob); var observed = store.SnapshotVersion();
        Assert.Equal("Visible before observation", store.GetAt(f.Bob, observed)?.SongTitle); // Reserving a heartbeat does not conceal the existing publication.
        f.Clock.Value = f.Clock.Value.AddSeconds(seconds);
        Assert.True(store.TrySet(new NowPlayingState { IdentityUserId = f.Bob, SongId = UserFactory.Song, IsPlaying = true, SongTitle = "Published after observation" }, TimeSpan.FromSeconds(90), pending));
        Assert.Null(store.GetAt(f.Bob, observed)); Assert.Equal("Published after observation", store.Get(f.Bob)?.SongTitle);
        var latest = store.SnapshotVersion(); Assert.Equal("Published after observation", store.GetAt(f.Bob, latest)?.SongTitle);
        store.Clear(f.Bob); Assert.Null(store.GetAt(f.Bob, latest)); // Clearing does not retain historical live-state copies.
    }
    [Fact]
    public async Task CursorRequiresRefreshAfterCacheInstanceRestartEvenWithTheSameProtectionKeys()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); await f.Db.History.InsertOneAsync(Listen(f, f.Bob)); using var alice = f.Client(f.Alice);
        var first = await alice.GetFromJsonAsync<JsonElement>("/api/feed/slides/page?limit=1"); var cursor = first.GetProperty("nextCursor").GetString()!;
        var protection = f.Services.GetRequiredService<IDataProtectionProvider>();
        using var restarted = f.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton(protection); }));
        Assert.NotEqual(f.Services.GetRequiredService<INowPlayingStore>().InstanceId, restarted.Services.GetRequiredService<INowPlayingStore>().InstanceId);
        using var current = restarted.CreateClient(); current.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Alice));
        var response = await current.GetAsync("/api/feed/slides/page?cursor=" + Uri.EscapeDataString(cursor)); Assert.Equal(HttpStatusCode.Gone, response.StatusCode); Assert.Contains("Refresh", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/api/feed/slides/page?limit=1")).StatusCode);
    }
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> GatedApp(UserFactory f, ProfileLookupGate gate)
    {
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("USER_TEST_MONGO")); settings.DirectConnection = true;
        settings.ClusterConfigurator = cluster => { cluster.Subscribe<CommandStartedEvent>(gate.Started); cluster.Subscribe<CommandSucceededEvent>(gate.Succeeded); };
        return f.WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<IMongoClient>(); services.AddSingleton<IMongoClient>(new MongoClient(settings)); }));
    }
    private sealed class ProfileLookupGate(string actor)
    {
        public bool Enabled; private int request; private int paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Started(CommandStartedEvent e) { if (Enabled && Volatile.Read(ref paused) == 0 && e.CommandName == "find" && e.Command["find"].AsString == "users" && e.Command["filter"].ToJson().Contains(actor, StringComparison.Ordinal)) request = e.RequestId; }
        public void Succeeded(CommandSucceededEvent e) { if (Enabled && request != 0 && e.RequestId == request && Interlocked.Exchange(ref paused, 1) == 0) { Entered.TrySetResult(); Release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } }
    }
    private sealed class PublicSelectionGate
    {
        public bool Enabled; private int request; private int paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Started(CommandStartedEvent e) { if (Enabled && e.CommandName == "find" && e.Command["find"].AsString == "users" && e.Command["filter"].ToJson().Contains("IsPrivate", StringComparison.Ordinal)) request = e.RequestId; }
        public void Succeeded(CommandSucceededEvent e) { if (Enabled && request != 0 && e.RequestId == request && Interlocked.Exchange(ref paused, 1) == 0) { Entered.TrySetResult(); Release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } }
    }
    private sealed class CatalogueGate : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = UserFactory.Song, title = "Canonical song", durationSec = 125, artists = new[] { new { name = "Canonical artist" } } }) }; }
    }
}
