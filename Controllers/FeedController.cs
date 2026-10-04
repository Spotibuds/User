using System.Globalization;
using MongoDB.Bson;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Services;
namespace User.Controllers;
[ApiController, Route("api/feed")]
public class FeedController(MongoDbContext db, ProfilePolicy profiles, HistoryService history, INowPlayingStore playing, TimeProvider clock, CanonicalSongReader songs, MongoTransactions transactions, INotificationService notifications, ILogger<FeedController> logger) : ControllerBase
{
    private static readonly SemaphoreSlim ReactionGate = new(1, 1);
    private string Actor => Input.Actor(User);
    private string WeekKey => HistoryService.WeekStart(clock.GetUtcNow().UtcDateTime).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    private static object SongPost(FeedItem item, Models.User user) => new { type = item.Type, postId = item.Id, identityUserId = user.IdentityUserId, username = user.UserName, displayName = user.DisplayName, songId = item.SongId, songTitle = item.SongTitle, artist = item.Artist, coverUrl = item.CoverUrl, playedAt = item.PlayedAt };
    private async Task<object> SongPost(FeedItem item) => SongPost(item, await profiles.Read(User, item.IdentityUserId, HttpContext.RequestAborted));
    [HttpGet("slides")]
    public async Task<object> Slides(string? identityUserId = null, int limit = 20, int skip = 0)
    {
        Input.Page(limit, skip); if (identityUserId != null) Input.Owner(User, identityUserId);
        var self = await profiles.Find(Actor);
        var ct = HttpContext.RequestAborted;
        var users = await db.Users.Find(x => !x.IsPrivate && x.IdentityUserId != Actor).SortBy(x => x.Id).Skip(skip / 4).Limit(limit).ToListAsync(ct);
        var ids = users.Select(x => x.IdentityUserId).ToList();
        var usersById = users.ToDictionary(x => x.IdentityUserId);
        var posts = await db.Feed.Find(x => ids.Contains(x.IdentityUserId) && x.Type == "recent_song").SortByDescending(x => x.PlayedAt).Limit(limit).ToListAsync(ct);
        var week = HistoryService.WeekStart(clock.GetUtcNow().UtcDateTime);
        var topByAccount = await history.ArtistsMany(ids.Append(self.IdentityUserId), week, ct);
        var songsByAccount = await TopSongsMany(ids, week, ct);
        var selfArtists = topByAccount[self.IdentityUserId].Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<object>();
        foreach (var state in playing.GetMany(ids).Where(x => x.IsPlaying)) result.Add(new { type = "now_playing", postId = $"nowplaying:{state.IdentityUserId}:{state.SongId}", state.IdentityUserId, username = usersById[state.IdentityUserId].UserName, state.SongId, state.SongTitle, state.Artist, state.CoverUrl, state.PositionSec, state.UpdatedAt });
        foreach (var post in posts) result.Add(SongPost(post, usersById[post.IdentityUserId]));
        foreach (var user in users)
        {
            var top = topByAccount[user.IdentityUserId];
            if (top.Count > 0) result.Add(new { type = "top_artists_week", postId = $"weekly:artists:{user.IdentityUserId}:{WeekKey}", identityUserId = user.IdentityUserId, username = user.UserName, displayName = user.DisplayName, topArtists = top });
            var topSongs = songsByAccount[user.IdentityUserId];
            if (topSongs.Count > 0) result.Add(new { type = "top_songs_week", postId = $"weekly:songs:{user.IdentityUserId}:{WeekKey}", identityUserId = user.IdentityUserId, username = user.UserName, displayName = user.DisplayName, topSongs });
            var common = top.Select(x => x.Name).Where(selfArtists.Contains).ToList();
            if (common.Count > 0) result.Add(new { type = "common_artists", postId = $"common:{Actor}:{user.IdentityUserId}:{WeekKey}", identityUserId = user.IdentityUserId, username = user.UserName, commonArtists = common, withIdentityUserId = Actor });
        }
        return result.Take(limit);
    }
    private async Task<Dictionary<string, List<TopSong>>> TopSongsMany(List<string> accounts, DateTime week, CancellationToken ct)
    {
        if (accounts.Count > 100) throw new ApiProblem(400, "Song aggregate supports at most 100 authors.");
        var pipeline = new BsonDocument[] {
            new("$match", new BsonDocument { { "IdentityUserId", new BsonDocument("$in", new BsonArray(accounts)) }, { "PlayedAt", new BsonDocument { { "$gte", week }, { "$lt", week.AddDays(7) } } } }),
            new("$group", new BsonDocument { { "_id", new BsonDocument { { "account", "$IdentityUserId" }, { "songId", "$SongId" }, { "songTitle", "$SongTitle" }, { "artist", "$Artist" } } }, { "count", new BsonDocument("$sum", 1) } }),
            new("$group", new BsonDocument { { "_id", "$_id.account" }, { "songs", new BsonDocument("$topN", new BsonDocument { { "n", 3 }, { "sortBy", new BsonDocument { { "count", -1 }, { "_id.songId", 1 } } }, { "output", new BsonDocument { { "songId", "$_id.songId" }, { "songTitle", "$_id.songTitle" }, { "artist", "$_id.artist" }, { "count", "$count" } } } }) } }) };
        var rows = await db.History.Aggregate<BsonDocument>(pipeline, new AggregateOptions { MaxTime = TimeSpan.FromSeconds(3) }).ToListAsync(ct);
        var result = accounts.ToDictionary(x => x, _ => new List<TopSong>());
        foreach (var row in rows) result[row["_id"].AsString] = row["songs"].AsBsonArray.Select(x => new TopSong { SongId = x["songId"].AsString, SongTitle = x["songTitle"].AsString, Artist = x["artist"].AsString, Count = x["count"].ToInt32() }).ToList();
        return result;
    }
    private async Task<List<TopSong>> TopSongs(string account, DateTime week) => await db.History.Aggregate().Match(x => x.IdentityUserId == account && x.PlayedAt >= week && x.PlayedAt < week.AddDays(7)).Group(x => new { x.SongId, x.SongTitle, x.Artist }, g => new TopSong { SongId = g.Key.SongId, SongTitle = g.Key.SongTitle, Artist = g.Key.Artist, Count = g.Count() }).SortByDescending(x => x.Count).Limit(3).ToListAsync();
    [HttpPost("nowplaying")]
    public async Task<object> Set(NowPlayingState state, int ttlSec = 90)
    {
        await profiles.Find(Actor); Input.ObjectId(state.SongId); if (state.PositionSec is < 0 or > 86400 || ttlSec is < 30 or > 180) throw new ApiProblem(400, "Invalid playback state.");
        state.IdentityUserId = Actor;
        if (!state.IsPlaying) playing.Clear(Actor);
        else { var song = await songs.Read(state.SongId, HttpContext.RequestAborted); if (state.PositionSec > song.DurationSec) throw new ApiProblem(400, "Position exceeds the track duration."); state.SongTitle = song.Title; state.Artist = CanonicalSongReader.ArtistNames(song); state.CoverUrl = song.CoverUrl; playing.Set(state, TimeSpan.FromSeconds(ttlSec)); }
        return new { success = true };
    }
    [HttpDelete("nowplaying/{identityUserId}")] public object Clear(string identityUserId) { Input.Owner(User, identityUserId); playing.Clear(Actor); return new { success = true }; }
    [HttpPost("nowplaying/batch")]
    public async Task<object> Batch(NowPlayingQuery query)
    {
        if (query.UserIds.Count > 50) throw new ApiProblem(400, "Maximum batch size is 50.");
        foreach (var id in query.UserIds) Input.GuidId(id);
        var users = await db.Users.Find(x => query.UserIds.Contains(x.IdentityUserId)).Limit(50).ToListAsync(); return playing.GetMany(users.Where(x => ProfilePolicy.Visible(User, x)).Select(x => x.IdentityUserId)).ToList();
    }
    [HttpGet("post")]
    public async Task<object> Post(string id)
    {
        if (id.Length > 200) throw new ApiProblem(400, "Invalid post identifier.");
        if (MongoDB.Bson.ObjectId.TryParse(id, out _)) return await SongPost(await db.Feed.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiProblem(404, "Post not found."));
        var parts = id.Split(':');
        if (parts.Length == 3 && parts[0] == "nowplaying") { var user = await profiles.Read(User, parts[1]); var state = playing.Get(user.IdentityUserId); if (state?.SongId != parts[2]) throw new ApiProblem(404, "Playback has ended."); return new { type = "now_playing", postId = id, state.IdentityUserId, username = user.UserName, state.SongId, state.SongTitle, state.Artist, state.CoverUrl, state.PositionSec }; }
        if (parts.Length != 4 || !DateTime.TryParseExact(parts[3], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var week) || week > clock.GetUtcNow().UtcDateTime || week < clock.GetUtcNow().UtcDateTime.AddDays(-90)) throw new ApiProblem(400, "Invalid post identifier or expired week.");
        var author = await profiles.Read(User, parts[2]);
        if (parts[0] == "weekly" && parts[1] == "artists") return new { type = "top_artists_week", postId = id, identityUserId = author.IdentityUserId, username = author.UserName, displayName = author.DisplayName, topArtists = await history.Artists(author.IdentityUserId, week) };
        if (parts[0] == "weekly" && parts[1] == "songs") return new { type = "top_songs_week", postId = id, identityUserId = author.IdentityUserId, username = author.UserName, displayName = author.DisplayName, topSongs = await TopSongs(author.IdentityUserId, week) };
        if (parts[0] == "common") { Input.Owner(User, parts[1]); var common = (await history.Artists(Actor, week)).Select(x => x.Name).Intersect((await history.Artists(author.IdentityUserId, week)).Select(x => x.Name), StringComparer.OrdinalIgnoreCase).ToList(); return new { type = "common_artists", postId = id, identityUserId = author.IdentityUserId, username = author.UserName, commonArtists = common, withIdentityUserId = Actor }; }
        throw new ApiProblem(404, "Post not found.");
    }
    private async Task<string> PostOwner(string postId)
    {
        if (MongoDB.Bson.ObjectId.TryParse(postId, out _)) return (await db.Feed.Find(x => x.Id == postId).FirstOrDefaultAsync() ?? throw new ApiProblem(404, "Post not found.")).IdentityUserId;
        var parts = postId.Split(':'); if (parts.Length == 3 && parts[0] == "nowplaying") return parts[1]; if (parts.Length == 4 && (parts[0] is "weekly" or "common")) return parts[2]; throw new ApiProblem(400, "Invalid post identifier.");
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
                if (request.ContextType == "now_playing" || postId?.StartsWith("nowplaying:", StringComparison.Ordinal) == true)
                {
                    var parts = postId?.Split(':');
                    var owner = SocialCommands.Account(parts is { Length: 3 } && parts[0] == "nowplaying" ? parts[1] : request.ToIdentityUserId);
                    var songId = parts is { Length: 3 } && parts[0] == "nowplaying" ? parts[2] : request.SongId;
                    if (songId == null) throw new ApiProblem(400, "Song identifier required.");
                    Input.ObjectId(songId);
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
                    return (added: false, notice: (Notification?)null, target: reference.Owner, changed: changed.ModifiedCount > 0);
                }
                var reaction = new Reaction { FromIdentityUserId = actor, FromUserName = self.UserName, ToIdentityUserId = reference.Owner, PostId = reference.PostId, Emoji = request.Emoji, ContextType = reference.ContextType, SongId = reference.SongId, SongTitle = reference.SongTitle, Artist = reference.Artist, CreatedAt = now };
                await db.Reactions.InsertOneAsync(session, reaction, cancellationToken: ct);
                Notification? notice = null;
                if (actor != reference.Owner && !await db.Notifications.Find(session, x => x.Key == noticeKey).AnyAsync(ct))
                    notice = await notifications.PersistAsync(session, new Notification { TargetUserId = reference.Owner, SourceUserId = actor, Type = NotificationType.Reaction, Title = $"{self.UserName} reacted to your music", Message = $"{reaction.Emoji} on your music post.", Key = noticeKey, Data = new() { ["reactionId"] = reaction.Id, ["postId"] = reference.PostId, ["actionPostId"] = reference.ActionPostId, ["emoji"] = reaction.Emoji }, ActionUrl = $"/feed/post/{Uri.EscapeDataString(reference.ActionPostId)}", CreatedAt = now, ExpiresAt = reference.ExpiresAt }, ct);
                return (added: true, notice, target: reference.Owner, changed: false);
            }, cancellationToken);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (result.notice != null) await notifications.PublishAsync(result.notice).WaitAsync(timeout.Token);
                else if (result.changed) await notifications.RefreshCountAsync(result.target).WaitAsync(timeout.Token);
            }
            catch (Exception ex) { logger.LogWarning("Committed reaction event could not be published ({ErrorType}). Clients recover through persisted state.", ex.GetType().Name); }
            return new { success = true, message = result.added ? "Reaction saved" : "Reaction removed", action = result.added ? "added" : "removed" };
        }
        finally { ReactionGate.Release(); }
    }
    [HttpGet("reactions/by-post")]
    public async Task<object> Reactions(string postId, string? currentUserId = null)
    {
        await Post(postId); var owner = await PostOwner(postId); await profiles.Read(User, owner);
        var parts = postId.Split(':');
        if (parts.Length == 3 && parts[0] == "nowplaying")
        {
            var key = $"recent_song:{owner}:{parts[2]}";
            var persisted = await db.Feed.Find(x => x.Key == key).FirstOrDefaultAsync(HttpContext.RequestAborted);
            if (persisted == null) return Array.Empty<Reaction>();
            postId = persisted.Id;
        }
        return await db.Reactions.Find(x => x.PostId == postId).SortByDescending(x => x.CreatedAt).Limit(100).ToListAsync(HttpContext.RequestAborted);
    }
    [HttpGet("reactions/latest")] public async Task<object> Latest(string identityUserId, int limit = 20, int skip = 0) { Input.Owner(User, identityUserId); Input.Page(limit, skip); return await db.Reactions.Find(x => x.ToIdentityUserId == Actor).SortByDescending(x => x.CreatedAt).Skip(skip).Limit(limit).ToListAsync(); }
    public class NowPlayingQuery { public List<string> UserIds { get; set; } = []; }
    public class TopSong { public string SongId { get; set; } = ""; public string SongTitle { get; set; } = ""; public string Artist { get; set; } = ""; public int Count { get; set; } }
}

