using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using User.Data;
using User.Entities;

namespace User.Services;

// Both the producer and notification validator resolve the same persisted or derived post.
public sealed record ReactionPostReference(string PostId, string Owner, string ContextType, string ActionPostId, string? SongId = null, string? SongTitle = null, string? Artist = null, DateTime? ExpiresAt = null)
{
    public static async Task<ReactionPostReference> Read(MongoDbContext db, IClientSessionHandle session, string postId, string viewer, TimeProvider clock, CancellationToken ct, bool allowPrivate = false)
    {
        if (string.IsNullOrWhiteSpace(postId) || postId.Length > 200) throw new ApiProblem(400, "Invalid post identifier.");
        ReactionPostReference result;
        if (ObjectId.TryParse(postId, out var id))
        {
            var post = await db.Feed.Find(session, x => x.Id == id.ToString()).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Post not found.");
            result = new(post.Id, post.IdentityUserId, post.Type, post.Id, post.SongId, post.SongTitle, post.Artist);
        }
        else
        {
            var parts = postId.Split(':');
            if (parts.Length != 4 || !DateTime.TryParseExact(parts[3], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var week) || HistoryService.WeekStart(week) != week || week > clock.GetUtcNow().UtcDateTime || week < clock.GetUtcNow().UtcDateTime.AddDays(-90)) throw new ApiProblem(400, "Invalid post identifier or expired week.");
            var owner = SocialCommands.Account(parts[2]);
            if (parts[0] == "weekly" && parts[1] is "artists" or "songs")
            {
                if (!await db.History.Find(session, x => x.IdentityUserId == owner && x.PlayedAt >= week && x.PlayedAt < week.AddDays(7)).AnyAsync(ct)) throw new ApiProblem(404, "Post not found.");
                var canonical = $"weekly:{parts[1]}:{owner}:{parts[3]}";
                result = new(canonical, owner, parts[1] == "artists" ? "top_artists_week" : "top_songs_week", canonical, ExpiresAt: week.AddDays(90));
            }
            else if (parts[0] == "common")
            {
                if (SocialCommands.Account(parts[1]) != viewer) throw new ApiProblem(403, "This comparison belongs to another account.");
                var mine = await Artists(db, session, viewer, week, ct);
                var theirs = await Artists(db, session, owner, week, ct);
                if (!mine.Intersect(theirs, StringComparer.OrdinalIgnoreCase).Any()) throw new ApiProblem(404, "Post not found.");
                // A personalized comparison cannot be opened by the recipient. Its action
                // points to the author's real weekly music post instead of a forbidden URL.
                result = new($"common:{viewer}:{owner}:{parts[3]}", owner, "common_artists", $"weekly:artists:{owner}:{parts[3]}", ExpiresAt: week.AddDays(90));
            }
            else throw new ApiProblem(404, "Post not found.");
        }
        var author = await db.Users.Find(session, x => x.IdentityUserId == result.Owner).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Post author no longer exists.");
        if (!allowPrivate && author.IsPrivate && author.IdentityUserId != viewer) throw new ApiProblem(403, "This profile is private.");
        return result;
    }
    private static async Task<List<string>> Artists(MongoDbContext db, IClientSessionHandle session, string account, DateTime week, CancellationToken ct)
    {
        var pipeline = new BsonDocument[] {
            new("$match", new BsonDocument { { "IdentityUserId", account }, { "PlayedAt", new BsonDocument { { "$gte", week }, { "$lt", week.AddDays(7) } } } }),
            new("$project", new BsonDocument("artists", new BsonDocument("$split", new BsonArray { "$Artist", "," }))),
            new("$unwind", "$artists"),
            new("$group", new BsonDocument { { "_id", new BsonDocument("$trim", new BsonDocument("input", "$artists")) }, { "count", new BsonDocument("$sum", 1) } }),
            new("$match", new BsonDocument("_id", new BsonDocument("$ne", ""))),
            new("$sort", new BsonDocument { { "count", -1 }, { "_id", 1 } }), new("$limit", 3)
        };
        var rows = await db.History.Aggregate<BsonDocument>(session, pipeline, new AggregateOptions { MaxTime = TimeSpan.FromSeconds(2) }).ToListAsync(ct);
        return rows.Select(x => x["_id"].AsString).ToList();
    }
}
