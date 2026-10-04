using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using User.Entities;
using User.Services;
using Xunit;

namespace User.Tests;

public sealed class FeedAppendRegressionTests
{
    [Fact]
    public async Task UppercaseLiveAliasResolvesTheSameDetailSummaryPeopleAndPersistedMutation()
    {
        using var f = new UserFactory(); await f.InitializeAsync();
        const string songId = "abcdefabcdefabcdefabcdef";
        var canonicalLive = $"nowplaying:{f.Bob}:{songId}";
        var alias = $"nowplaying:{f.Bob.ToUpperInvariant()}:{songId.ToUpperInvariant()}";
        f.Services.GetRequiredService<INowPlayingStore>().Set(new NowPlayingState { IdentityUserId = f.Bob, SongId = songId, SongTitle = "Canonical playback", Artist = "Canonical artist", IsPlaying = true }, TimeSpan.FromSeconds(180));
        using var alice = f.Client(f.Alice);
        using var detailResponse = await alice.GetAsync("/api/feed/post?id=" + Uri.EscapeDataString(alias)); Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(canonicalLive, detail.GetProperty("postId").GetString()); Assert.Equal(songId, detail.GetProperty("songId").GetString());
        var empty = await alice.GetFromJsonAsync<JsonElement>("/api/feed/reactions/summary?postId=" + Uri.EscapeDataString(alias)); Assert.Equal(canonicalLive, empty.GetProperty("postId").GetString()); Assert.Equal(0, empty.GetProperty("total").GetInt32());
        using var added = await alice.PostAsJsonAsync("/api/feed/reactions", new { postId = alias, emoji = "❤️", contextType = "now_playing", toIdentityUserId = f.Mallory, songId = UserFactory.Song }); Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var canonical = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("postId").GetString(); Assert.True(ObjectId.TryParse(canonical, out _));
        var row = await f.Db.Reactions.Find(_ => true).SingleAsync(); Assert.Equal(canonical, row.PostId); Assert.Equal(songId, row.SongId); Assert.Equal(f.Alice, row.FromIdentityUserId); Assert.Equal(f.Bob, row.ToIdentityUserId);
        foreach (var id in new[] { alias, canonicalLive, canonical! })
        {
            var summary = await alice.GetFromJsonAsync<JsonElement>("/api/feed/reactions/summary?postId=" + Uri.EscapeDataString(id)); Assert.Equal(canonical, summary.GetProperty("postId").GetString()); Assert.Equal(1, summary.GetProperty("total").GetInt32()); Assert.Equal("❤️", Assert.Single(summary.GetProperty("myEmojis").EnumerateArray()).GetString());
            var people = await alice.GetFromJsonAsync<JsonElement>("/api/feed/reactions/people?postId=" + Uri.EscapeDataString(id)); Assert.Equal(row.Id, Assert.Single(people.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        }
        using var removed = await alice.PostAsJsonAsync("/api/feed/reactions", new { postId = alias, emoji = "❤️" }); Assert.Equal(HttpStatusCode.OK, removed.StatusCode); Assert.Equal(canonical, (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("postId").GetString()); Assert.Equal(0, await f.Db.Reactions.CountDocumentsAsync(_ => true));
        // The retained legacy no-postId input must normalize its song id through the same branch.
        using var legacy = await alice.PostAsJsonAsync("/api/feed/reactions", new { contextType = "now_playing", toIdentityUserId = f.Bob.ToUpperInvariant(), songId = songId.ToUpperInvariant(), emoji = "👍" }); Assert.Equal(HttpStatusCode.OK, legacy.StatusCode); Assert.Equal(canonical, (await legacy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("postId").GetString());
        Assert.Equal(1, await f.Db.Feed.CountDocumentsAsync(_ => true)); Assert.Equal(songId, (await f.Db.Reactions.Find(_ => true).SingleAsync()).SongId);
    }

    [Fact]
    public async Task DelayedAppendKeepsNewestMillisecondTupleAndMigratesReactionsWithoutLosingHistory()
    {
        using var f = new UserFactory(); await f.InitializeAsync();
        const string songId = "abcdefabcdefabcdefabcdef";
        var millisecond = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero);
        f.Clock.Value = millisecond.AddTicks(1000);
        var catalogue = new SnapshotCatalogue(); var gate = new AppendProfileGate(f.Bob);
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("USER_TEST_MONGO")); settings.DirectConnection = true;
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(gate.Started);
        using var app = f.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMongoClient>(); services.AddSingleton<IMongoClient>(new MongoClient(settings));
            services.AddHttpClient("Music").ConfigurePrimaryHttpMessageHandler(() => catalogue);
        }));
        using var bob = app.CreateClient(); bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Token(f.Bob));
        gate.Enabled = true;
        var older = bob.PostAsJsonAsync($"/api/users/{f.Bob}/listening-history", new { songId, duration = 12 });
        FeedItem latest;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Both timestamps persist as the same BSON millisecond; the later ObjectId must win.
            f.Clock.Value = millisecond.AddTicks(8000); catalogue.Title = "Newer canonical title"; catalogue.Artist = "Newer canonical artist";
            Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync($"/api/users/{f.Bob}/listening-history", new { songId = songId.ToUpperInvariant(), duration = 12 })).StatusCode);
            latest = await f.Db.Feed.Find(_ => true).SingleAsync(); Assert.Equal("Newer canonical title", latest.SongTitle);
            Assert.Equal(songId, latest.SongId); Assert.Equal($"recent_song:{f.Bob}:{songId}", latest.Key);
            Assert.Equal(millisecond.UtcDateTime, latest.PlayedAt);
            await f.Db.Reactions.InsertOneAsync(new Reaction { PostId = $"nowplaying:{f.Bob}:{songId}", FromIdentityUserId = f.Alice, ToIdentityUserId = f.Bob, FromUserName = "alice", SongId = songId, Emoji = "👍", ContextType = "now_playing" });
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await older).StatusCode);
        var events = await f.Db.History.Find(_ => true).ToListAsync(); Assert.Equal(2, events.Count);
        Assert.Equal(new[] { "Older canonical title", "Newer canonical title" }.Order(), events.Select(x => x.SongTitle).Order());
        Assert.All(events, item => Assert.Equal(millisecond.UtcDateTime, item.PlayedAt));
        Assert.All(events, item => Assert.Equal(songId, item.SongId));
        var kept = await f.Db.Feed.Find(_ => true).SingleAsync();
        Assert.Equal(latest.Id, kept.Id); Assert.Equal("Newer canonical title", kept.SongTitle); Assert.Equal("Newer canonical artist", kept.Artist);
        Assert.Equal(events.Max(x => x.Id), kept.LastHistoryEventId);
        Assert.Equal(kept.Id, (await f.Db.Reactions.Find(_ => true).SingleAsync()).PostId);
        var raw = await f.Db.Database.GetCollection<BsonDocument>(f.Db.Feed.CollectionNamespace.CollectionName).Find(new BsonDocument("_id", ObjectId.Parse(kept.Id))).SingleAsync();
        Assert.Equal(kept.LastHistoryEventId, raw["lastHistoryEventId"].AsString); Assert.False(raw.Contains("LastHistoryEventId"));

        // An older timestamp is recorded without moving the shared post backwards.
        f.Clock.Value = millisecond.AddMilliseconds(-1); catalogue.Title = "Older clock title";
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync($"/api/users/{f.Bob}/listening-history", new { songId, duration = 12 })).StatusCode);
        kept = await f.Db.Feed.Find(_ => true).SingleAsync(); Assert.Equal(latest.Id, kept.Id); Assert.Equal(latest.LastHistoryEventId, kept.LastHistoryEventId); Assert.Equal(latest.PlayedAt, kept.PlayedAt); Assert.Equal(latest.SongTitle, kept.SongTitle);

        // A pre-migration post has no source id: reject older listens, then adopt an equal-time source once.
        var legacyTime = millisecond.AddMilliseconds(2).UtcDateTime;
        await f.Db.Feed.UpdateOneAsync(x => x.Id == kept.Id, Builders<FeedItem>.Update.Unset(x => x.LastHistoryEventId).Set(x => x.PlayedAt, legacyTime).Set(x => x.SongTitle, "Legacy title"));
        f.Clock.Value = millisecond.AddMilliseconds(1); catalogue.Title = "Too old for legacy";
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync($"/api/users/{f.Bob}/listening-history", new { songId, duration = 12 })).StatusCode);
        var legacy = await f.Db.Feed.Find(_ => true).SingleAsync(); Assert.Null(legacy.LastHistoryEventId); Assert.Equal("Legacy title", legacy.SongTitle);
        f.Clock.Value = millisecond.AddMilliseconds(2).AddTicks(8000); catalogue.Title = "Adopted equal-time source";
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync($"/api/users/{f.Bob}/listening-history", new { songId, duration = 12 })).StatusCode);
        var adopted = await f.Db.Feed.Find(_ => true).SingleAsync(); Assert.Equal(latest.Id, adopted.Id); Assert.Equal(legacyTime, adopted.PlayedAt); Assert.Equal("Adopted equal-time source", adopted.SongTitle); Assert.NotNull(adopted.LastHistoryEventId);
        Assert.Equal(5, await f.Db.History.CountDocumentsAsync(_ => true)); Assert.Equal(1, await f.Db.Feed.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task SaturatedRecentPhaseReturnsAll250TiedPostsBeforeEveryWeeklyCardFor12Authors()
    {
        using var f = new UserFactory(); await f.InitializeAsync(); f.Clock.Value = new DateTimeOffset(2030, 4, 7, 12, 0, 0, TimeSpan.Zero);
        var extra = Enumerable.Range(0, 10).Select(i => new Models.User { IdentityUserId = Guid.NewGuid().ToString(), UserName = "author" + i }).ToList();
        await f.Db.Users.InsertManyAsync(extra);
        var authors = new[] { f.Bob, f.Mallory }.Concat(extra.Select(x => x.IdentityUserId)).ToList();
        var posts = Enumerable.Range(0, 250).Select(i =>
        {
            var actor = authors[i % authors.Count]; var song = ObjectId.GenerateNewId().ToString();
            return new FeedItem { IdentityUserId = actor, Key = $"recent_song:{actor}:{song}", Type = "recent_song", SongId = song, SongTitle = "Saved listen " + i, Artist = "Shared artist", PlayedAt = f.Clock.Value.UtcDateTime.AddSeconds(-(i / 25)) };
        }).ToList();
        await f.Db.Feed.InsertManyAsync(posts);
        await f.Db.History.InsertManyAsync(authors.Append(f.Alice).Select(actor => new HistoryEvent { IdentityUserId = actor, SongId = UserFactory.Song, SongTitle = "Shared listen", Artist = "Shared artist", ArtistNames = ["Shared artist"], PlayedAt = f.Clock.Value.UtcDateTime, Duration = 12 }));
        using var alice = f.Client(f.Alice); var items = new List<JsonElement>(); string? cursor = null; var ended = false;
        for (var page = 0; page < 80; page++)
        {
            using var response = await alice.GetAsync("/api/feed/slides/page?limit=17" + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            var batch = data.GetProperty("items").EnumerateArray().Select(x => x.Clone()).ToList(); Assert.InRange(batch.Count, 0, 17); items.AddRange(batch);
            if (!data.GetProperty("hasMore").GetBoolean()) { Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind); ended = true; break; }
            var next = data.GetProperty("nextCursor").GetString(); Assert.False(string.IsNullOrEmpty(next)); Assert.NotEqual(cursor, next); cursor = next;
        }
        Assert.True(ended, "The bounded fixture must reach the end of its weekly phase.");
        Assert.Equal(286, items.Count); Assert.Equal(items.Count, items.Select(x => x.GetProperty("postId").GetString()).Distinct().Count());
        Assert.Equal(posts.OrderByDescending(x => x.PlayedAt).ThenByDescending(x => x.Id).Select(x => x.Id), items.Take(250).Select(x => x.GetProperty("postId").GetString()));
        Assert.All(items.Take(250), item => Assert.Equal("recent_song", item.GetProperty("type").GetString()));
        foreach (var type in new[] { "top_artists_week", "top_songs_week", "common_artists" })
        {
            var weekly = items.Skip(250).Where(x => x.GetProperty("type").GetString() == type).ToList(); Assert.Equal(12, weekly.Count);
            Assert.Equal(authors.Order(), weekly.Select(x => x.GetProperty("identityUserId").GetString()!).Order());
        }
        Assert.DoesNotContain(items, item => item.GetProperty("identityUserId").GetString() == f.Alice);
    }

    private sealed class AppendProfileGate(string actor)
    {
        public bool Enabled; private int paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Started(CommandStartedEvent e)
        {
            if (!Enabled || e.CommandName != "find" || e.Command["find"].AsString != "users" || !e.Command.Contains("startTransaction") || !e.Command["filter"].ToJson().Contains(actor, StringComparison.Ordinal) || Interlocked.CompareExchange(ref paused, 1, 0) != 0) return;
            Entered.TrySetResult(); Release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }
    private sealed class SnapshotCatalogue : HttpMessageHandler
    {
        public string Title { get; set; } = "Older canonical title";
        public string Artist { get; set; } = "Older canonical artist";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = request.RequestUri!.Segments[^1], title = Title, durationSec = 125, artists = new[] { new { name = Artist } }, coverUrl = (string?)null }) });
    }
}
