using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Services;
namespace User.Controllers;
[ApiController, Route("api/feed")]
public class FeedController(MongoDbContext db, ProfilePolicy profiles, INowPlayingStore playing, TimeProvider clock, CanonicalSongReader songs, MongoTransactions transactions, INotificationService notifications, ILogger<FeedController> logger, FeedPager pager, IDataProtectionProvider protection) : ControllerBase
{
    private static readonly SemaphoreSlim ReactionGate = new(1, 1);
    private string Actor => Input.Actor(User);
    [HttpGet("slides/page")]
    public Task<FeedPage> Page(string? identityUserId = null, int limit = 20, string? cursor = null)
    {
        if (identityUserId != null) Input.Owner(User, identityUserId);
        return pager.Read(Actor, limit, cursor, HttpContext.RequestAborted);
    }
    [HttpGet("slides")]
    public async Task<object> Slides(string? identityUserId = null, int limit = 20, int skip = 0)
    {
        Input.Page(limit, skip); if (identityUserId != null) Input.Owner(User, identityUserId);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); budget.CancelAfter(TimeSpan.FromSeconds(10));
        var result = new List<object>(); string? cursor = null;
        try
        {
            do
            {
                var page = await pager.Read(Actor, Math.Min(100, skip + limit - result.Count), cursor, budget.Token);
                foreach (var item in page.Items) { if (skip > 0) skip--; else result.Add(item); }
                cursor = page.NextCursor;
            } while (cursor != null && result.Count < limit);
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested) { throw new ApiProblem(503, "Feed paging timed out. Retry, or use the continuation endpoint."); }
        return result;
    }
    [HttpPost("nowplaying")]
    public async Task<object> Set(NowPlayingState state, int ttlSec = 90)
    {
        Input.ObjectId(state.SongId); state.SongId = ObjectId.Parse(state.SongId).ToString(); if (state.PositionSec is < 0 or > 86400 || ttlSec is < 30 or > 180) throw new ApiProblem(400, "Invalid playback state.");
        state.IdentityUserId = Actor;
        var reservation = playing.Reserve(Actor); await profiles.Find(Actor);
        if (!state.IsPlaying) playing.TryClear(Actor, reservation);
        else { var song = await songs.Read(state.SongId, HttpContext.RequestAborted); await profiles.Find(Actor); if (state.PositionSec > song.DurationSec) throw new ApiProblem(400, "Position exceeds the track duration."); state.SongTitle = song.Title; state.Artist = CanonicalSongReader.ArtistNames(song); state.CoverUrl = song.CoverUrl; playing.TrySet(state, TimeSpan.FromSeconds(ttlSec), reservation); }
        return new { success = true };
    }
    [HttpDelete("nowplaying/{identityUserId}")] public object Clear(string identityUserId) { Input.Owner(User, identityUserId); playing.Clear(Actor); return new { success = true }; }
    [HttpPost("nowplaying/batch")]
    public async Task<object> Batch(NowPlayingQuery query)
    {
        if (query.UserIds == null || query.UserIds.Count > 50) throw new ApiProblem(400, "Maximum batch size is 50.");
        foreach (var id in query.UserIds) Input.GuidId(id);
        var publicationVersion = playing.SnapshotVersion();
        var ids = query.UserIds.Select(SocialCommands.Account).Distinct().ToList(); var users = await db.Users.Find(x => ids.Contains(x.IdentityUserId)).Limit(50).ToListAsync(HttpContext.RequestAborted); return users.Where(x => ProfilePolicy.Visible(User, x)).Select(x => playing.GetAt(x.IdentityUserId, publicationVersion)).Where(x => x != null).ToList();
    }
    private async Task<(NowPlayingState State, Models.User Author, string Id)> ReadLive(IClientSessionHandle session, string id, CancellationToken ct)
    {
        var parts = id.Split(':');
        if (parts.Length != 3 || parts[0] != "nowplaying") throw new ApiProblem(400, "Invalid post identifier.");
        var account = SocialCommands.Account(parts[1]); Input.ObjectId(parts[2]); var song = ObjectId.Parse(parts[2]).ToString();
        var publicationVersion = playing.SnapshotVersion();
        var author = await db.Users.Find(session, x => x.IdentityUserId == account).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Post author no longer exists.");
        if (!ProfilePolicy.Visible(User, author)) throw new ApiProblem(403, "This profile is private.");
        var state = playing.GetAt(account, publicationVersion);
        if (state is not { IsPlaying: true } || state.SongId != song) throw new ApiProblem(404, "Playback has ended or changed. Reload the post.");
        return (state, author, $"nowplaying:{account}:{song}");
    }
    [HttpGet("post")]
    public async Task<object> Post(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) throw new ApiProblem(400, "Invalid post identifier.");
        return await transactions.Run<object>(async (session, ct) =>
        {
            if (id.StartsWith("nowplaying:", StringComparison.Ordinal)) { var live = await ReadLive(session, id, ct); return FeedPager.LivePost(live.State, live.Author); }
            var reference = await ReactionPostReference.Read(db, session, id, Actor, clock, ct, User.IsInRole("Admin"));
            var author = await db.Users.Find(session, x => x.IdentityUserId == reference.Owner).FirstAsync(ct);
            if (ObjectId.TryParse(reference.PostId, out _)) return FeedPager.SongPost(await db.Feed.Find(session, x => x.Id == reference.PostId).FirstAsync(ct), author);
            var parts = reference.PostId.Split(':'); var week = DateTime.ParseExact(parts[3], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (reference.ContextType == "top_artists_week") return new { type = reference.ContextType, postId = reference.PostId, identityUserId = author.IdentityUserId, username = author.UserName, displayName = author.DisplayName, topArtists = await HistoryService.ArtistsInSnapshot(db, session, author.IdentityUserId, week, null, ct) };
            if (reference.ContextType == "top_songs_week") return new { type = reference.ContextType, postId = reference.PostId, identityUserId = author.IdentityUserId, username = author.UserName, displayName = author.DisplayName, topSongs = (await FeedPager.TopSongs(db, session, [author.IdentityUserId], week, null, ct))[author.IdentityUserId] };
            var mine = await HistoryService.ArtistsInSnapshot(db, session, Actor, week, null, ct); var theirs = await HistoryService.ArtistsInSnapshot(db, session, author.IdentityUserId, week, null, ct);
            return new { type = "common_artists", postId = reference.PostId, identityUserId = author.IdentityUserId, username = author.UserName, commonArtists = theirs.Select(x => x.Name).Intersect(mine.Select(x => x.Name), StringComparer.OrdinalIgnoreCase).ToList(), withIdentityUserId = Actor };
        }, HttpContext.RequestAborted);
    }
    private async Task<string> ReactionPost(IClientSessionHandle session, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) throw new ApiProblem(400, "Invalid post identifier.");
        if (!id.StartsWith("nowplaying:", StringComparison.Ordinal)) return (await ReactionPostReference.Read(db, session, id, Actor, clock, ct, User.IsInRole("Admin"))).PostId;
        var live = await ReadLive(session, id, ct); var key = $"recent_song:{live.Author.IdentityUserId}:{live.State.SongId}";
        return (await db.Feed.Find(session, x => x.Key == key).FirstOrDefaultAsync(ct))?.Id ?? live.Id;
    }
    [HttpPost("reactions")]
    public async Task<object> React(Reaction request)
    {
        if (!new[] { "👍", "❤️", "😂", "😮", "🔥", "👏" }.Contains(request.Emoji)) throw new ApiProblem(400, "Choose a supported reaction.");
        var actor = SocialCommands.Account(Actor); var cancellationToken = HttpContext.RequestAborted;
        await ReactionGate.WaitAsync(cancellationToken);
        try
        {
            var result = await transactions.Run(async (session, ct) =>
            {
                var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()).UtcDateTime;
                var postId = request.PostId;
                if (postId?.StartsWith("nowplaying:", StringComparison.Ordinal) == true || postId == null && request.ContextType == "now_playing")
                {
                    var parts = postId?.Split(':');
                    if (postId != null && (parts is not { Length: 3 } || parts[0] != "nowplaying")) throw new ApiProblem(400, "Invalid post identifier.");
                    var owner = SocialCommands.Account(parts is { Length: 3 } && parts[0] == "nowplaying" ? parts[1] : request.ToIdentityUserId);
                    var songId = parts is { Length: 3 } && parts[0] == "nowplaying" ? parts[2] : request.SongId;
                    if (songId == null) throw new ApiProblem(400, "Song identifier required.");
                    Input.ObjectId(songId); songId = ObjectId.Parse(songId).ToString();
                    var author = await db.Users.Find(session, x => x.IdentityUserId == owner).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Post author no longer exists.");
                    if (!ProfilePolicy.Visible(User, author)) throw new ApiProblem(403, "This profile is private.");
                    var state = playing.Get(owner);
                    if (state == null || !state.IsPlaying || state.SongId != songId) throw new ApiProblem(404, "Playback has ended.");
                    var key = $"recent_song:{owner}:{state.SongId}";
                    var post = await db.Feed.FindOneAndUpdateAsync(session, Builders<FeedItem>.Filter.Eq(x => x.Key, key), Builders<FeedItem>.Update.SetOnInsert(x => x.Key, key).SetOnInsert(x => x.IdentityUserId, owner).SetOnInsert(x => x.Type, "recent_song").SetOnInsert(x => x.SongId, state.SongId).SetOnInsert(x => x.SongTitle, state.SongTitle).SetOnInsert(x => x.Artist, state.Artist).SetOnInsert(x => x.CoverUrl, state.CoverUrl).SetOnInsert(x => x.PlayedAt, now), new FindOneAndUpdateOptions<FeedItem, FeedItem> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
                    postId = post.Id;
                }
                var reference = await ReactionPostReference.Read(db, session, postId ?? "", actor, clock, ct, User.IsInRole("Admin"));
                var accounts = new[] { actor, reference.Owner }.Distinct().ToList();
                var current = await db.Users.Find(session, x => accounts.Contains(x.IdentityUserId)).ToListAsync(ct);
                if (current.Count != accounts.Count) throw new ApiProblem(404, "A participant profile is missing.");
                foreach (var profile in current.OrderBy(x => x.Id, StringComparer.Ordinal))
                {
                    var touchedAt = profile.UpdatedAt is DateTime previous && previous >= now ? previous.AddMilliseconds(1) : now;
                    var touched = await db.Users.UpdateOneAsync(session, x => x.Id == profile.Id && x.IdentityUserId == profile.IdentityUserId, Builders<Models.User>.Update.Set(x => x.UpdatedAt, touchedAt), cancellationToken: ct);
                    if (touched.MatchedCount != 1) throw new ApiProblem(404, "A participant profile is missing.");
                }
                var self = current.Single(x => x.IdentityUserId == actor);
                var noticeKey = $"reaction:{reference.PostId}:{actor}:{reference.Owner}";
                var existing = await db.Reactions.FindOneAndDeleteAsync(session, x => x.PostId == reference.PostId && x.FromIdentityUserId == actor && x.Emoji == request.Emoji, cancellationToken: ct);
                if (existing != null)
                {
                    var remaining = await db.Reactions.Find(session, x => x.PostId == reference.PostId && x.FromIdentityUserId == actor).SortBy(x => x.Id).FirstOrDefaultAsync(ct);
                    var filter = Builders<Notification>.Filter.Where(x => x.Key == noticeKey && x.Type == NotificationType.Reaction && x.Status != NotificationStatus.Handled);
                    // Retain one attention record per actor/post. Its live reference follows
                    // a remaining reaction; removing the final reaction terminalizes it.
                    var update = remaining == null
                        ? Builders<Notification>.Update.Set(x => x.Status, NotificationStatus.Handled).Set(x => x.HandledAt, now)
                        : Builders<Notification>.Update.Set("Data.reactionId", remaining.Id).Set("Data.emoji", remaining.Emoji).Set(x => x.Message, $"{remaining.Emoji} on your music post.");
                    var changed = await db.Notifications.UpdateManyAsync(session, filter, update, cancellationToken: ct);
                    return (added: false, notice: (Notification?)null, target: reference.Owner, changed: changed.ModifiedCount > 0, postId: reference.PostId);
                }
                var reaction = new Reaction { FromIdentityUserId = actor, FromUserName = self.UserName, ToIdentityUserId = reference.Owner, PostId = reference.PostId, Emoji = request.Emoji, ContextType = reference.ContextType, SongId = reference.SongId, SongTitle = reference.SongTitle, Artist = reference.Artist, CreatedAt = now };
                await db.Reactions.InsertOneAsync(session, reaction, cancellationToken: ct);
                Notification? notice = null;
                if (actor != reference.Owner && !await db.Notifications.Find(session, x => x.Key == noticeKey).AnyAsync(ct))
                    notice = await notifications.PersistAsync(session, new Notification { TargetUserId = reference.Owner, SourceUserId = actor, Type = NotificationType.Reaction, Title = $"{self.UserName} reacted to your music", Message = $"{reaction.Emoji} on your music post.", Key = noticeKey, Data = new() { ["reactionId"] = reaction.Id, ["postId"] = reference.PostId, ["actionPostId"] = reference.ActionPostId, ["emoji"] = reaction.Emoji }, ActionUrl = $"/feed/post/{Uri.EscapeDataString(reference.ActionPostId)}", CreatedAt = now, ExpiresAt = reference.ExpiresAt }, ct);
                return (added: true, notice, target: reference.Owner, changed: false, postId: reference.PostId);
            }, cancellationToken);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (result.notice != null) await notifications.PublishAsync(result.notice).WaitAsync(timeout.Token);
                else if (result.changed) await notifications.RefreshCountAsync(result.target).WaitAsync(timeout.Token);
            }
            catch (Exception ex) { logger.LogWarning("Committed reaction event could not be published ({ErrorType}). Clients recover through persisted state.", ex.GetType().Name); }
            return new { success = true, message = result.added ? "Reaction saved" : "Reaction removed", action = result.added ? "added" : "removed", postId = result.postId };
        }
        finally { ReactionGate.Release(); }
    }
    private readonly IDataProtector reactionProtector = protection.CreateProtector("Spotibuds.Feed.Reactions.v1");
    private sealed class ReactionPosition
    {
        public string Actor { get; set; } = ""; public string Post { get; set; } = "";
        public DateTime AsOf { get; set; } public DateTime CreatedAt { get; set; } public string Id { get; set; } = "";
    }
    [HttpGet("reactions/summary")]
    public async Task<object> Summary(string postId) => await transactions.Run<object>(async (session, ct) =>
    {
        var canonical = await ReactionPost(session, postId, ct);
        var pipeline = new BsonDocument[] { new("$match", new BsonDocument("postId", canonical)), new("$group", new BsonDocument { { "_id", "$emoji" }, { "count", new BsonDocument("$sum", 1) } }), new("$sort", new BsonDocument("_id", 1)) };
        var rows = await db.Reactions.Aggregate<BsonDocument>(session, pipeline, new AggregateOptions { MaxTime = TimeSpan.FromSeconds(3) }).ToListAsync(ct);
        var mine = await db.Reactions.Find(session, x => x.PostId == canonical && x.FromIdentityUserId == Actor).ToListAsync(ct);
        return new { postId = canonical, total = rows.Sum(x => x["count"].ToInt64()), counts = rows.Select(x => new { emoji = x["_id"].AsString, count = x["count"].ToInt64() }).ToList(), myEmojis = mine.Select(x => x.Emoji).Distinct().Order(StringComparer.Ordinal).ToList() };
    }, HttpContext.RequestAborted);
    [HttpGet("reactions/people")]
    public async Task<object> People(string postId, int limit = 20, string? cursor = null)
    {
        Input.Page(limit, 0); ReactionPosition? position = null;
        if (cursor != null)
        {
            try { if (cursor.Length > 4096) throw new FormatException(); position = JsonSerializer.Deserialize<ReactionPosition>(reactionProtector.Unprotect(cursor)) ?? throw new FormatException(); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or JsonException) { throw new ApiProblem(410, "This reaction continuation is no longer available. Reload the people list."); }
            if (position.Actor != Actor || position.AsOf < clock.GetUtcNow().UtcDateTime.AddMinutes(-10) || position.AsOf > clock.GetUtcNow().UtcDateTime || !ObjectId.TryParse(position.Id, out _)) throw new ApiProblem(410, "This reaction continuation has expired. Reload the people list.");
        }
        return await transactions.Run<object>(async (session, ct) =>
        {
            var asOf = position?.AsOf ?? clock.GetUtcNow().UtcDateTime;
            var canonical = await ReactionPost(session, postId, ct);
            if (position != null && position.Post != canonical) throw new ApiProblem(410, "This continuation belongs to another post. Reload the people list.");
            var filter = Builders<Reaction>.Filter.Where(x => x.PostId == canonical && x.CreatedAt <= asOf);
            var total = await db.Reactions.CountDocumentsAsync(session, filter, new CountOptions { MaxTime = TimeSpan.FromSeconds(3) }, ct);
            if (position != null) filter &= Builders<Reaction>.Filter.Lt(x => x.CreatedAt, position.CreatedAt) | (Builders<Reaction>.Filter.Eq(x => x.CreatedAt, position.CreatedAt) & Builders<Reaction>.Filter.Lt(x => x.Id, position.Id));
            var rows = await db.Reactions.Find(session, filter).SortByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Limit(limit + 1).ToListAsync(ct);
            var more = rows.Count > limit; var items = rows.Take(limit).ToList(); string? next = null;
            if (more) { var last = items[^1]; next = reactionProtector.Protect(JsonSerializer.Serialize(new ReactionPosition { Actor = Actor, Post = canonical, AsOf = asOf, CreatedAt = last.CreatedAt, Id = last.Id })); }
            return new { items, nextCursor = next, hasMore = more, total };
        }, HttpContext.RequestAborted);
    }
    [HttpGet("reactions/by-post")]
    public async Task<object> Reactions(string postId, string? currentUserId = null) => await transactions.Run<object>(async (session, ct) =>
    {
        var canonical = await ReactionPost(session, postId, ct);
        return await db.Reactions.Find(session, x => x.PostId == canonical).SortByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Limit(100).ToListAsync(ct);
    }, HttpContext.RequestAborted);
    [HttpGet("reactions/latest")]
    public async Task<object> Latest(string identityUserId, int limit = 20, int skip = 0)
    {
        Input.Owner(User, identityUserId); Input.Page(limit, skip);
        return await db.Reactions.Find(x => x.ToIdentityUserId == Actor).SortByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip(skip).Limit(limit).ToListAsync(HttpContext.RequestAborted);
    }
    public class NowPlayingQuery { public List<string> UserIds { get; set; } = []; }
    public class TopSong { public string SongId { get; set; } = ""; public string SongTitle { get; set; } = ""; public string Artist { get; set; } = ""; public int Count { get; set; } }
}

