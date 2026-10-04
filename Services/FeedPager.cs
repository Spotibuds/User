using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using User.Controllers;
using User.Data;
using User.Entities;

namespace User.Services;

public sealed record FeedPage(List<object> Items, string? NextCursor, bool HasMore);

// Positions belong to content phases, never to an estimated number of cards per author.
public sealed class FeedPager(MongoDbContext db, HistoryService history, INowPlayingStore playing, TimeProvider clock, MongoTransactions transactions, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Spotibuds.Feed.Page.v1");
    private sealed class Position
    {
        public string Actor { get; set; } = "";
        public DateTime AsOf { get; set; }
        public DateTime Week { get; set; }
        public long PublicationVersion { get; set; }
        public string InstanceId { get; set; } = "";
        public string MaxAuthor { get; set; } = "";
        public int Phase { get; set; }
        public string? Author { get; set; }
        public int Card { get; set; }
        public DateTime? PlayedAt { get; set; }
        public string? Post { get; set; }
    }
    public async Task<FeedPage> Read(string actor, int limit, string? cursor, CancellationToken ct)
    {
        Input.Page(limit, 0);
        Position? supplied = null;
        if (cursor != null)
        {
            try { if (cursor.Length > 4096) throw new FormatException(); supplied = JsonSerializer.Deserialize<Position>(protector.Unprotect(cursor)) ?? throw new FormatException(); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or JsonException) { throw new ApiProblem(410, "This feed continuation is no longer available. Refresh the feed."); }
            if (supplied.Actor != actor || supplied.InstanceId != playing.InstanceId || supplied.AsOf > clock.GetUtcNow().UtcDateTime || supplied.AsOf < clock.GetUtcNow().UtcDateTime.AddMinutes(-10) || supplied.Week != HistoryService.WeekStart(supplied.AsOf) || supplied.Phase is < 0 or > 3 || supplied.Card is < 0 or > 3 || !ObjectId.TryParse(supplied.MaxAuthor, out _)) throw new ApiProblem(410, "This feed continuation has expired or the feed service restarted. Refresh the feed.");
        }
        return await transactions.Run(async (session, token) =>
        {
            var publicationVersion = playing.SnapshotVersion(); // Cache publication and Mongo authorization share an observation boundary.
            var now = clock.GetUtcNow().UtcDateTime;
            // Retry callbacks must restart from the supplied position, not partially advanced state.
            var p = supplied == null ? null : JsonSerializer.Deserialize<Position>(JsonSerializer.Serialize(supplied));
            if (!await db.Users.Find(session, x => x.IdentityUserId == actor).AnyAsync(token)) throw new ApiProblem(404, "Profile not found.");
            if (p == null)
            {
                var last = await db.Users.Find(session, x => x.IdentityUserId != actor).SortByDescending(x => x.Id).FirstOrDefaultAsync(token);
                if (last == null) return new FeedPage([], null, false);
                p = new Position { Actor = actor, AsOf = now, Week = HistoryService.WeekStart(now), MaxAuthor = last.Id, PublicationVersion = publicationVersion, InstanceId = playing.InstanceId };
            }
            var items = new List<object>(); var scanned = 0;
            while (items.Count < limit && p.Phase < 3 && scanned < 400)
            {
                if (p.Phase == 1)
                {
                    var match = new BsonDocument { { "type", "recent_song" }, { "playedAt", new BsonDocument { { "$lte", p.AsOf }, { "$ne", BsonNull.Value } } } };
                    if (p.PlayedAt != null) match["$or"] = new BsonArray { new BsonDocument("playedAt", new BsonDocument("$lt", p.PlayedAt.Value)), new BsonDocument { { "playedAt", p.PlayedAt.Value }, { "_id", new BsonDocument("$lt", ObjectId.Parse(p.Post!)) } } };
                    var pipeline = new BsonDocument[] {
                        new("$match", match), new("$sort", new BsonDocument { { "playedAt", -1 }, { "_id", -1 } }),
                        new("$lookup", new BsonDocument { { "from", db.Users.CollectionNamespace.CollectionName }, { "localField", "identityUserId" }, { "foreignField", "IdentityUserId" }, { "as", "author" } }),
                        new("$unwind", "$author"),
                        new("$match", new BsonDocument { { "author.IsPrivate", false }, { "author.IdentityUserId", new BsonDocument("$ne", actor) }, { "author._id", new BsonDocument("$lte", ObjectId.Parse(p.MaxAuthor)) } }),
                        new("$limit", limit - items.Count) };
                    var posts = await db.Feed.Aggregate<BsonDocument>(session, pipeline, new AggregateOptions { MaxTime = TimeSpan.FromSeconds(3) }).ToListAsync(token);
                    foreach (var row in posts)
                    {
                        var author = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Models.User>(row["author"].AsBsonDocument); row.Remove("author");
                        var post = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<FeedItem>(row);
                        items.Add(SongPost(post, author)); p.PlayedAt = post.PlayedAt; p.Post = post.Id;
                    }
                    if (posts.Count < limit - (items.Count - posts.Count)) { p.Phase = 2; p.Author = null; p.Card = 0; }
                    continue;
                }
                var filter = Builders<Models.User>.Filter.Where(x => !x.IsPrivate && x.IdentityUserId != actor) & Builders<Models.User>.Filter.Lte(x => x.Id, p.MaxAuthor);
                if (p.Author != null) filter &= p.Phase == 2 && p.Card > 0 ? Builders<Models.User>.Filter.Gte(x => x.Id, p.Author) : Builders<Models.User>.Filter.Gt(x => x.Id, p.Author);
                var authors = await db.Users.Find(session, filter).SortBy(x => x.Id).Limit(Math.Min(100, 400 - scanned)).ToListAsync(token);
                if (authors.Count == 0) { p.Phase++; p.Author = null; p.Card = 0; continue; }
                scanned += authors.Count;
                var ids = authors.Select(x => x.IdentityUserId).ToList();
                var top = p.Phase == 2 ? await history.ArtistsMany(ids.Append(actor), p.Week, token, session, p.AsOf) : null;
                var songs = p.Phase == 2 ? await TopSongs(db, session, ids, p.Week, p.AsOf, token) : null;
                var mine = top?[actor].Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var user in authors)
                {
                    if (p.Phase == 0)
                    {
                        p.Author = user.Id;
                        var state = playing.GetAt(user.IdentityUserId, p.PublicationVersion);
                        if (state is { IsPlaying: true }) items.Add(LivePost(state, user));
                    }
                    else
                    {
                        var start = p.Author == user.Id ? p.Card : 0;
                        var artists = top![user.IdentityUserId]; var weeklySongs = songs![user.IdentityUserId]; var common = artists.Select(x => x.Name).Where(mine!.Contains).ToList();
                        var weekKey = p.Week.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                        for (var card = start; card < 3 && items.Count < limit; card++)
                        {
                            p.Author = user.Id; p.Card = card + 1;
                            if (card == 0 && artists.Count > 0) items.Add(new { type = "top_artists_week", postId = $"weekly:artists:{user.IdentityUserId}:{weekKey}", identityUserId = user.IdentityUserId, username = user.UserName, displayName = user.DisplayName, topArtists = artists });
                            if (card == 1 && weeklySongs.Count > 0) items.Add(new { type = "top_songs_week", postId = $"weekly:songs:{user.IdentityUserId}:{weekKey}", identityUserId = user.IdentityUserId, username = user.UserName, displayName = user.DisplayName, topSongs = weeklySongs });
                            if (card == 2 && common.Count > 0) items.Add(new { type = "common_artists", postId = $"common:{actor}:{user.IdentityUserId}:{weekKey}", identityUserId = user.IdentityUserId, username = user.UserName, commonArtists = common, withIdentityUserId = actor });
                        }
                        if (p.Card == 3) p.Card = 0;
                    }
                    if (items.Count == limit) break;
                }
            }
            var more = p.Phase < 3;
            return new FeedPage(items, more ? protector.Protect(JsonSerializer.Serialize(p)) : null, more);
        }, ct);
    }
    public static object SongPost(FeedItem item, Models.User user) => new { type = item.Type, postId = item.Id, identityUserId = user.IdentityUserId, username = user.UserName, displayName = user.DisplayName, songId = item.SongId, songTitle = item.SongTitle, artist = item.Artist, coverUrl = item.CoverUrl, playedAt = item.PlayedAt };
    public static object LivePost(NowPlayingState state, Models.User user) => new { type = "now_playing", postId = $"nowplaying:{state.IdentityUserId}:{state.SongId}", state.IdentityUserId, username = user.UserName, state.SongId, state.SongTitle, state.Artist, state.CoverUrl, state.PositionSec, state.UpdatedAt };
    public static async Task<Dictionary<string, List<FeedController.TopSong>>> TopSongs(MongoDbContext db, IClientSessionHandle session, List<string> accounts, DateTime week, DateTime? asOf, CancellationToken ct)
    {
        var date = new BsonDocument { { "$gte", week }, { "$lt", week.AddDays(7) } }; if (asOf != null) date["$lte"] = asOf.Value;
        var pipeline = new BsonDocument[] {
            new("$match", new BsonDocument { { "IdentityUserId", new BsonDocument("$in", new BsonArray(accounts)) }, { "PlayedAt", date } }),
            new("$sort", new BsonDocument { { "PlayedAt", -1 }, { "_id", -1 } }),
            new("$group", new BsonDocument { { "_id", new BsonDocument { { "account", "$IdentityUserId" }, { "songId", "$SongId" } } }, { "songTitle", new BsonDocument("$first", "$SongTitle") }, { "artist", new BsonDocument("$first", "$Artist") }, { "count", new BsonDocument("$sum", 1) } }),
            new("$group", new BsonDocument { { "_id", "$_id.account" }, { "songs", new BsonDocument("$topN", new BsonDocument { { "n", 3 }, { "sortBy", new BsonDocument { { "count", -1 }, { "_id.songId", 1 } } }, { "output", new BsonDocument { { "songId", "$_id.songId" }, { "songTitle", "$songTitle" }, { "artist", "$artist" }, { "count", "$count" } } } }) } }) };
        var rows = await db.History.Aggregate<BsonDocument>(session, pipeline, new AggregateOptions { MaxTime = TimeSpan.FromSeconds(3) }).ToListAsync(ct);
        var result = accounts.ToDictionary(x => x, _ => new List<FeedController.TopSong>());
        foreach (var row in rows) result[row["_id"].AsString] = row["songs"].AsBsonArray.Select(x => new FeedController.TopSong { SongId = x["songId"].AsString, SongTitle = x["songTitle"].AsString, Artist = x["artist"].AsString, Count = x["count"].ToInt32() }).ToList();
        return result;
    }
}
