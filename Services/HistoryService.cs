using MongoDB.Bson;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Controllers;
using User.Models;
namespace User.Services;
public sealed class HistoryService(MongoDbContext db, TimeProvider clock, MongoTransactions transactions, CanonicalSongReader songs)
{
    public async Task<Dictionary<string, List<TopArtist>>> ArtistsMany(IEnumerable<string> accounts, DateTime? start = null, CancellationToken ct = default)
    {
        var ids = accounts.Distinct().ToList();
        if (ids.Count > 101) throw new ApiProblem(400, "Artist aggregates support at most 101 accounts.");
        foreach (var id in ids) Input.GuidId(id);
        var week = start ?? WeekStart(clock.GetUtcNow().UtcDateTime);
        var pipeline = new BsonDocument[] {
            new("$match", new BsonDocument { { "IdentityUserId", new BsonDocument("$in", new BsonArray(ids)) }, { "PlayedAt", new BsonDocument { { "$gte", week }, { "$lt", week.AddDays(7) } } } }),
            new("$project", new BsonDocument { { "account", "$IdentityUserId" }, { "artists", new BsonDocument("$split", new BsonArray { "$Artist", "," }) } }),
            new("$unwind", "$artists"),
            new("$group", new BsonDocument { { "_id", new BsonDocument { { "account", "$account" }, { "name", new BsonDocument("$trim", new BsonDocument("input", "$artists")) } } }, { "count", new BsonDocument("$sum", 1) } }),
            new("$match", new BsonDocument("_id.name", new BsonDocument("$ne", ""))),
            new("$group", new BsonDocument { { "_id", "$_id.account" }, { "artists", new BsonDocument("$topN", new BsonDocument { { "n", 3 }, { "sortBy", new BsonDocument { { "count", -1 }, { "_id.name", 1 } } }, { "output", new BsonDocument { { "name", "$_id.name" }, { "count", "$count" } } } }) } }) };
        var rows = await db.History.Aggregate<BsonDocument>(pipeline, new AggregateOptions { MaxTime = TimeSpan.FromSeconds(3) }).ToListAsync(ct);
        var result = ids.ToDictionary(x => x, _ => new List<TopArtist>());
        foreach (var row in rows) result[row["_id"].AsString] = row["artists"].AsBsonArray.Select(x => new TopArtist { Name = x["name"].AsString, Count = x["count"].ToInt32() }).ToList();
        return result;
    }
    public static DateTime WeekStart(DateTime utc) => utc.Date.AddDays(-(int)utc.DayOfWeek); // Sunday 00:00 UTC, half-open seven-day window.
    public async Task Append(string actor, AddListeningHistoryDto dto, CancellationToken ct)
    {
        Input.ObjectId(dto.SongId); Input.Text(dto.SongTitle, 200); Input.Text(dto.Artist, 5000);
        if (dto.Duration is < 0 or > 86400) throw new ApiProblem(400, "Invalid listened duration.");
        var song = await songs.Read(dto.SongId, ct);
        if (dto.Duration > song.DurationSec) throw new ApiProblem(400, "Listened duration exceeds the track duration.");
        dto.SongTitle = song.Title; dto.Artist = CanonicalSongReader.ArtistNames(song); dto.CoverUrl = song.CoverUrl;
        var item = new HistoryEvent { IdentityUserId = actor, SongId = dto.SongId, SongTitle = dto.SongTitle.Trim(), Artist = dto.Artist.Trim(), Duration = dto.Duration, CoverUrl = dto.CoverUrl, PlayedAt = clock.GetUtcNow().UtcDateTime };
        await transactions.Run(async (session, token) =>
        {
            await db.History.InsertOneAsync(session, item, cancellationToken: token);
            var key = $"recent_song:{actor}:{dto.SongId}";
            var post = await db.Feed.FindOneAndUpdateAsync(session, Builders<FeedItem>.Filter.Eq(x => x.Key, key), Builders<FeedItem>.Update.SetOnInsert(x => x.IdentityUserId, actor).SetOnInsert(x => x.Type, "recent_song").SetOnInsert(x => x.Key, key).Set(x => x.SongId, dto.SongId).Set(x => x.SongTitle, dto.SongTitle).Set(x => x.Artist, dto.Artist).Set(x => x.CoverUrl, dto.CoverUrl).Set(x => x.PlayedAt, item.PlayedAt), new FindOneAndUpdateOptions<FeedItem> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, token);
            var legacy = await db.Reactions.Find(session, x => x.ToIdentityUserId == actor && x.SongId == dto.SongId && (x.PostId == $"nowplaying:{actor}:{dto.SongId}" || x.PostId == null)).Limit(1000).ToListAsync(token);
            foreach (var reaction in legacy)
            {
                if (await db.Reactions.Find(session, x => x.PostId == post.Id && x.FromIdentityUserId == reaction.FromIdentityUserId && x.Emoji == reaction.Emoji).AnyAsync(token))
                    await db.Reactions.DeleteOneAsync(session, x => x.Id == reaction.Id, cancellationToken: token);
                else await db.Reactions.UpdateOneAsync(session, x => x.Id == reaction.Id, Builders<Reaction>.Update.Set(x => x.PostId, post.Id), cancellationToken: token);
            }
            return true;
        }, ct);
    }
    public async Task<List<ListeningHistoryItem>> Read(string actor, int limit, int skip, CancellationToken ct)
    {
        Input.Page(limit, skip);
        var events = await db.History.Find(x => x.IdentityUserId == actor).SortByDescending(x => x.PlayedAt).ThenByDescending(x => x.Id).Skip(skip).Limit(limit).ToListAsync(ct);
        return events.Select(x => new ListeningHistoryItem { SongId = x.SongId, SongTitle = x.SongTitle, Artist = x.Artist, CoverUrl = x.CoverUrl, PlayedAt = x.PlayedAt, Duration = x.Duration }).ToList();
    }
    public async Task<List<TopArtist>> Artists(string actor, DateTime? start = null, CancellationToken ct = default)
    {
        var week = start ?? WeekStart(clock.GetUtcNow().UtcDateTime);
        var pipeline = new BsonDocument[] {
            new("$match", new BsonDocument { { "IdentityUserId", actor }, { "PlayedAt", new BsonDocument { { "$gte", week }, { "$lt", week.AddDays(7) } } } }),
            new("$project", new BsonDocument("artists", new BsonDocument("$split", new BsonArray { "$Artist", "," }))), new("$unwind", "$artists"),
            new("$group", new BsonDocument { { "_id", new BsonDocument("$trim", new BsonDocument("input", "$artists")) }, { "count", new BsonDocument("$sum", 1) } }),
            new("$sort", new BsonDocument { { "count", -1 }, { "_id", 1 } }), new("$limit", 3) };
        var rows = await db.History.Aggregate<BsonDocument>(pipeline).ToListAsync(ct);
        return rows.Select(x => new TopArtist { Name = x["_id"].AsString, Count = x["count"].ToInt32() }).ToList();
    }
}
