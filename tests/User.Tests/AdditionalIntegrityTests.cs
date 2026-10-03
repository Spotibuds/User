using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using SkiaSharp;
using User.Entities;
using User.Services;
using Xunit;

namespace User.Tests;

public sealed class StorageFactAttribute : FactAttribute
{
    public StorageFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUSIC_TEST_STORAGE"))) Skip = "Set MUSIC_TEST_STORAGE to this task's isolated fresh Azurite connection to execute actual avatar tests.";
    }
}

public sealed class AdditionalIntegrityTests
{
    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(12, 12); bitmap.Erase(SKColors.MediumPurple);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static MultipartFormDataContent Image(byte[] bytes, string filename = "misleading.jpg")
    {
        var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(bytes), "file", filename); return form;
    }
    [StorageFact]
    public async Task ActualAvatarBytesRejectSpoofedTruncatedOversizedContentPreservePriorAndEnforcePrivacy()
    {
        using var factory = new UserFactory(Environment.GetEnvironmentVariable("MUSIC_TEST_STORAGE")); await factory.InitializeAsync();
        using var alice = factory.Client(factory.Alice); using var mallory = factory.Client(factory.Mallory); using var anonymous = factory.Client();
        var created = new List<string>();
        try
        {
            using var denied = Image(Png());
            Assert.Equal(HttpStatusCode.Forbidden, (await mallory.PostAsync($"/api/users/{factory.Alice}/profile-picture", denied)).StatusCode);
            Assert.Null((await factory.Db.Users.Find(x => x.IdentityUserId == factory.Alice).SingleAsync()).AvatarUrl);
            using var valid = Image(Png());
            var response = await alice.PostAsync($"/api/users/{factory.Alice}/profile-picture", valid);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(); var url = json.GetProperty("avatarUrl").GetString()!; created.Add(url);
            var path = new Uri(url).PathAndQuery;
            var bytesResponse = await anonymous.GetAsync(path); Assert.Equal(HttpStatusCode.OK, bytesResponse.StatusCode); Assert.Equal("image/png", bytesResponse.Content.Headers.ContentType!.MediaType);
            var bytes = await bytesResponse.Content.ReadAsByteArrayAsync(); using (var decoded = SKBitmap.Decode(bytes)) Assert.NotNull(decoded);
            foreach (var payload in new[] { new byte[] { 1, 2, 3 }, Png()[..40], new byte[5 * 1024 * 1024 + 1] })
            {
                using var invalid = Image(payload, "spoofed.png");
                Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync($"/api/users/{factory.Alice}/profile-picture", invalid)).StatusCode);
                Assert.Equal(url, (await factory.Db.Users.Find(x => x.IdentityUserId == factory.Alice).SingleAsync()).AvatarUrl);
                Assert.Equal(bytes, await anonymous.GetByteArrayAsync(path));
            }
            using (var info = new SKBitmap(4097, 1))
            using (var image = SKImage.FromBitmap(info))
            using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var dimensions = Image(encoded.ToArray()))
                Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync($"/api/users/{factory.Alice}/profile-picture", dimensions)).StatusCode);
            await factory.Db.Users.UpdateOneAsync(x => x.IdentityUserId == factory.Alice, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsJsonAsync($"/api/users/{factory.Alice}", new { avatarUrl = (string?)null })).StatusCode);
            Assert.Null((await factory.Db.Users.Find(x => x.IdentityUserId == factory.Alice).SingleAsync()).AvatarUrl);
            Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(path)).StatusCode);
        }
        finally
        {
            var blobs = (AzureBlobService)factory.Services.GetRequiredService<IAzureBlobService>();
            foreach (var url in created) await blobs.Delete(url);
        }
    }
    [Fact]
    public async Task HistoryAndPlaybackUseCanonicalCatalogueMetadataAndRejectUnknownOrExcessiveProgress()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice);
        var historyPath = $"/api/users/{factory.Alice}/listening-history";
        var forged = new { songId = UserFactory.Song, songTitle = "Forged title", artist = "Forged artist", coverUrl = "https://attacker.invalid/track.png", duration = 12 };
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync(historyPath, forged)).StatusCode);
        var history = await factory.Db.History.Find(_ => true).SingleAsync(); var post = await factory.Db.Feed.Find(_ => true).SingleAsync();
        Assert.Equal("Test song", history.SongTitle); Assert.Equal("Test artist", history.Artist); Assert.Null(history.CoverUrl);
        Assert.Equal("Test song", post.SongTitle); Assert.Equal("Test artist", post.Artist); Assert.Null(post.CoverUrl);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/feed/nowplaying", new { identityUserId = factory.Bob, songId = UserFactory.Song, songTitle = "Forged title", artist = "Forged artist", coverUrl = "https://attacker.invalid/track.png", isPlaying = true, positionSec = 1 })).StatusCode);
        var playing = factory.Services.GetRequiredService<INowPlayingStore>(); var current = playing.Get(factory.Alice)!;
        Assert.Equal("Test song", current.SongTitle); Assert.Equal("Test artist", current.Artist); Assert.Null(current.CoverUrl); Assert.Null(playing.Get(factory.Bob));
        const string unknown = "222222222222222222222222";
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync(historyPath, new { songId = unknown, songTitle = "Forged title", artist = "Forged artist", duration = 12 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/feed/nowplaying", new { songId = unknown, isPlaying = true, positionSec = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync(historyPath, new { songId = UserFactory.Song, songTitle = "Forged title", artist = "Forged artist", duration = factory.Dependencies.SongDuration + 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/feed/nowplaying", new { songId = UserFactory.Song, isPlaying = true, positionSec = factory.Dependencies.SongDuration + 1 })).StatusCode);
        factory.Dependencies.SongStatus = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alice.PostAsJsonAsync(historyPath, forged)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alice.PostAsJsonAsync("/api/feed/nowplaying", new { songId = UserFactory.Song, isPlaying = true, positionSec = 2 })).StatusCode);
        Assert.Equal(1, await factory.Db.History.CountDocumentsAsync(_ => true)); Assert.Equal(1, await factory.Db.Feed.CountDocumentsAsync(_ => true)); Assert.Equal(post.Id, (await factory.Db.Feed.Find(_ => true).SingleAsync()).Id);
        Assert.Equal(1, playing.Get(factory.Alice)!.PositionSec); Assert.Equal(UserFactory.Song, playing.Get(factory.Alice)!.SongId);
    }
    [StorageFact]
    public async Task ConcurrentAvatarReplacementPublishesOneWinnerAndRemovesUnreferencedLoserBytes()
    {
        var storage = Environment.GetEnvironmentVariable("MUSIC_TEST_STORAGE")!;
        AvatarUploadBarrier? barrier = null;
        using var factory = new UserFactory(storage, blobs => barrier = new AvatarUploadBarrier(blobs)); await factory.InitializeAsync();
        using var alice = factory.Client(factory.Alice);
        try
        {
            using var first = Image(Png()); using var second = Image(Png());
            var responses = await Task.WhenAll(alice.PostAsync($"/api/users/{factory.Alice}/profile-picture", first), alice.PostAsync($"/api/users/{factory.Alice}/profile-picture", second));
            Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK)); Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            var response = await responses.Single(x => x.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<JsonElement>();
            var winner = response.GetProperty("avatarUrl").GetString()!;
            Assert.Equal(winner, (await factory.Db.Users.Find(x => x.IdentityUserId == factory.Alice).SingleAsync()).AvatarUrl);
            Assert.Equal(2, barrier!.Urls.Count);
            var loser = barrier.Urls.Single(url => url != winner);
            Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(new Uri(winner).PathAndQuery)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(new Uri(loser).PathAndQuery)).StatusCode);
            var blobs = new BlobServiceClient(storage).GetBlobContainerClient("avatars");
            Assert.True((await blobs.GetBlobClient(factory.Alice + "/" + new Uri(winner).Segments.Last()).ExistsAsync()).Value);
            Assert.False((await blobs.GetBlobClient(factory.Alice + "/" + new Uri(loser).Segments.Last()).ExistsAsync()).Value);
            Assert.Equal(0, await factory.Db.AvatarCleanup.CountDocumentsAsync(_ => true));
        }
        finally
        {
            if (barrier is not null) foreach (var url in barrier.Urls) await barrier.Delete(url);
        }
    }
    [Fact]
    public async Task InvalidProfileJsonTypesReturn400WithNoPersistedChanges()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice);
        foreach (var payload in new[] { "[]", "null", "{\"bio\":5}", "{\"displayName\":{}}", "{\"userName\":[]}", "{\"isPrivate\":\"yes\"}", "{\"avatarUrl\":\"https://attacker.invalid/private.png\"}" })
        {
            using var body = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsync($"/api/users/{factory.Alice}", body)).StatusCode);
            var user = await factory.Db.Users.Find(x => x.IdentityUserId == factory.Alice).SingleAsync();
            Assert.Equal("original", user.Bio); Assert.Equal("Alice", user.DisplayName); Assert.Equal("alice", user.UserName); Assert.False(user.IsPrivate); Assert.Null(user.AvatarUrl);
        }
    }
    [Fact]
    public async Task MongoTransactionAbortDoesNotLeaveHistoryFeedOrReactionSideEffects()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync();
        var transaction = factory.Services.GetRequiredService<MongoTransactions>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.Run<bool>(async (session, ct) =>
        {
            await factory.Db.History.InsertOneAsync(session, new HistoryEvent { IdentityUserId = factory.Alice, SongId = UserFactory.Song, SongTitle = "rollback", Artist = "Artist" }, cancellationToken: ct);
            await factory.Db.Feed.InsertOneAsync(session, new FeedItem { Key = "rollback", IdentityUserId = factory.Alice, Type = "recent_song" }, cancellationToken: ct);
            await factory.Db.Reactions.InsertOneAsync(session, new Reaction { PostId = "rollback", FromIdentityUserId = factory.Bob, ToIdentityUserId = factory.Alice, Emoji = "❤️" }, cancellationToken: ct);
            throw new InvalidOperationException("Injected interruption after transaction writes.");
        }));
        Assert.Equal(0, await factory.Db.History.CountDocumentsAsync(_ => true)); Assert.Equal(0, await factory.Db.Feed.CountDocumentsAsync(_ => true)); Assert.Equal(0, await factory.Db.Reactions.CountDocumentsAsync(_ => true));
    }
    [Fact]
    public async Task ReactionsUseClaimActorCanonicalPersistedPostAndRejectPrivateOrExpiredPlayback()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice); using var bob = factory.Client(factory.Bob); using var mallory = factory.Client(factory.Mallory);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/feed/nowplaying", new { songId = UserFactory.Song, songTitle = "Test song", artist = "Test artist", isPlaying = true, positionSec = 1 })).StatusCode);
        var livePost = $"nowplaying:{factory.Alice}:{UserFactory.Song}";
        Assert.Empty(await bob.GetFromJsonAsync<List<Reaction>>("/api/feed/reactions/by-post?postId=" + livePost) ?? []);
        Assert.Equal(0, await factory.Db.Feed.CountDocumentsAsync(_ => true));
        var request = new { emoji = "❤️", contextType = "now_playing", songId = UserFactory.Song, toIdentityUserId = factory.Alice, fromIdentityUserId = factory.Mallory, fromUserName = "spoofed" };
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync("/api/feed/reactions", request)).StatusCode);
        var reaction = await factory.Db.Reactions.Find(_ => true).SingleAsync(); var post = await factory.Db.Feed.Find(_ => true).SingleAsync();
        Assert.Equal(factory.Bob, reaction.FromIdentityUserId); Assert.Equal("bob", reaction.FromUserName); Assert.Equal(post.Id, reaction.PostId); Assert.Equal("recent_song", reaction.ContextType);
        var liveReactions = await bob.GetFromJsonAsync<List<Reaction>>("/api/feed/reactions/by-post?postId=" + livePost);
        Assert.Equal(reaction.Id, Assert.Single(liveReactions!).Id);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/api/users/{factory.Alice}/listening-history", new { songId = UserFactory.Song, songTitle = "Test song", artist = "Test artist", duration = 2 })).StatusCode);
        Assert.Equal(post.Id, (await factory.Db.Reactions.Find(_ => true).SingleAsync()).PostId);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/feed/post?id=" + post.Id)).StatusCode);
        Assert.Equal(reaction.Id, Assert.Single((await bob.GetFromJsonAsync<List<Reaction>>("/api/feed/reactions/by-post?postId=" + post.Id))!).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync("/api/feed/reactions", new { emoji = "not-supported", postId = post.Id })).StatusCode);
        await factory.Db.Users.UpdateOneAsync(x => x.IdentityUserId == factory.Alice, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.PostAsJsonAsync("/api/feed/reactions", new { emoji = "🔥", postId = post.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.GetAsync("/api/feed/reactions/by-post?postId=" + post.Id)).StatusCode);
        Assert.Equal(1, await factory.Db.Reactions.CountDocumentsAsync(_ => true));
        await factory.Db.Users.UpdateOneAsync(x => x.IdentityUserId == factory.Alice, Builders<Models.User>.Update.Set(x => x.IsPrivate, false));
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/feed/nowplaying/{factory.Alice}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync("/api/feed/reactions", request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/feed/reactions/by-post?postId=" + livePost)).StatusCode);
        Assert.Equal(reaction.Id, Assert.Single((await bob.GetFromJsonAsync<List<Reaction>>("/api/feed/reactions/by-post?postId=" + post.Id))!).Id);
        Assert.Equal(1, await factory.Db.Reactions.CountDocumentsAsync(_ => true));
    }
    [Fact]
    public async Task BatchedFeedAggregatesIncludeOnlyPublicAuthorsAndTopThreeWithinUtcWeek()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync(); using var alice = factory.Client(factory.Alice);
        var now = factory.Clock.GetUtcNow().UtcDateTime; var week = HistoryService.WeekStart(now);
        await factory.Db.Users.UpdateOneAsync(x => x.IdentityUserId == factory.Mallory, Builders<Models.User>.Update.Set(x => x.IsPrivate, true));
        var events = new List<HistoryEvent>();
        foreach (var account in new[] { factory.Alice, factory.Bob, factory.Mallory })
            for (var artist = 0; artist < 5; artist++) for (var listen = 0; listen < 5 - artist; listen++)
                events.Add(new HistoryEvent { IdentityUserId = account, SongId = $"{artist + 1:x24}", SongTitle = "Track " + artist, Artist = "Artist " + artist, PlayedAt = now });
        events.Add(new HistoryEvent { IdentityUserId = factory.Bob, SongId = UserFactory.Song, SongTitle = "Previous week", Artist = "Excluded", PlayedAt = week.AddTicks(-1) });
        await factory.Db.History.InsertManyAsync(events);
        var history = factory.Services.GetRequiredService<HistoryService>();
        var batches = await history.ArtistsMany(new[] { factory.Alice, factory.Bob, factory.Mallory });
        Assert.Equal(3, batches[factory.Bob].Count); Assert.Equal(new[] { "Artist 0", "Artist 1", "Artist 2" }, batches[factory.Bob].Select(x => x.Name)); Assert.Equal(new[] { 5, 4, 3 }, batches[factory.Bob].Select(x => x.Count));
        var slides = await alice.GetAsync("/api/feed/slides?limit=100"); Assert.Equal(HttpStatusCode.OK, slides.StatusCode);
        var posts = await slides.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(posts.EnumerateArray(), x => x.GetProperty("identityUserId").GetString() == factory.Mallory);
        var songs = posts.EnumerateArray().Single(x => x.GetProperty("type").GetString() == "top_songs_week").GetProperty("topSongs"); Assert.Equal(3, songs.GetArrayLength());
        Assert.Equal("Track 0", songs[0].GetProperty("songTitle").GetString());
        Assert.DoesNotContain(posts.EnumerateArray().Select(x => x.ToString()), x => x.Contains("Excluded", StringComparison.Ordinal));
    }
}

// Both real uploads finish after their controller has read the same profile version.
// Releasing them together deterministically tests the production compare-and-swap.
public sealed class AvatarUploadBarrier(IAzureBlobService inner) : IAzureBlobService
{
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrived;
    public ConcurrentBag<string> Urls { get; } = [];
    public async Task<string> UploadUserProfilePictureAsync(string userId, Stream imageStream, string fileName)
    {
        var url = await inner.UploadUserProfilePictureAsync(userId, imageStream, fileName); Urls.Add(url);
        if (Interlocked.Increment(ref arrived) == 2) gate.TrySetResult();
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(15)); return url;
    }
    public Task<Stream> DownloadAvatar(string userId, string filename) => inner.DownloadAvatar(userId, filename);
    public Task Cleanup(string url) => inner.Cleanup(url);
    public Task Published(string url) => inner.Published(url);
    public Task Delete(string url) => inner.Delete(url);
    public Task Ready(CancellationToken ct) => inner.Ready(ct);
}
