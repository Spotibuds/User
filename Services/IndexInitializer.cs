using MongoDB.Driver;
using User.Data;
using User.Entities;
namespace User.Services;
public sealed class IndexState { public volatile bool Ready; }
public sealed class IndexInitializer(MongoDbContext db, ILogger<IndexInitializer> logger, IndexState state) : BackgroundService
{
    public static Task<UpdateResult> MigrateNotificationTimestamps(MongoDbContext db, CancellationToken ct = default)
    {
        // Old notifications shadowed BaseEntity.CreatedAt and stored both fields.
        // The capitalized field was the producer's timestamp; retain it atomically.
        var collection = db.Database.GetCollection<MongoDB.Bson.BsonDocument>("notifications");
        var filter = new MongoDB.Bson.BsonDocument("CreatedAt", new MongoDB.Bson.BsonDocument("$type", "date"));
        var pipeline = new MongoDB.Bson.BsonDocument[] {
            new("$set", new MongoDB.Bson.BsonDocument("createdAt", "$CreatedAt")),
            new("$unset", "CreatedAt")
        };
        return collection.UpdateManyAsync(filter, new PipelineUpdateDefinition<MongoDB.Bson.BsonDocument>(pipeline), cancellationToken: ct);
    }

    public static async Task EnsureNotificationTimeline(MongoDbContext db, CancellationToken ct = default)
    {
        using var cursor = await db.Notifications.Indexes.ListAsync(ct);
        var existing = (await cursor.ToListAsync(ct)).FirstOrDefault(x => x["name"] == "notification_timeline");
        var legacy = new MongoDB.Bson.BsonDocument { { "TargetUserId", 1 }, { "CreatedAt", -1 }, { "_id", -1 } };
        if (existing != null && existing["key"].AsBsonDocument == legacy)
            await db.Notifications.Indexes.DropOneAsync("notification_timeline", ct);
        await db.Notifications.Indexes.CreateOneAsync(new CreateIndexModel<Notification>(Builders<Notification>.IndexKeys.Ascending(x => x.TargetUserId).Descending(x => x.CreatedAt).Descending(x => x.Id), new CreateIndexOptions { Name = "notification_timeline" }), cancellationToken: ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (int attempt = 1; attempt <= 12 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await MigrateNotificationTimestamps(db, stoppingToken);
                await db.Users.Indexes.CreateOneAsync(new CreateIndexModel<Models.User>(Builders<Models.User>.IndexKeys.Ascending(x => x.IdentityUserId), new CreateIndexOptions { Unique = true, Name = "identity_user_unique" }), cancellationToken: stoppingToken);
                await db.Friends.Indexes.CreateOneAsync(new CreateIndexModel<Friend>(Builders<Friend>.IndexKeys.Ascending(x => x.PairKey), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await db.Friends.Indexes.CreateOneAsync(new CreateIndexModel<Friend>(Builders<Friend>.IndexKeys.Ascending(x => x.RequestId), new CreateIndexOptions { Unique = true, Sparse = true, Name = "friend_request_identity" }), cancellationToken: stoppingToken);
                await db.Chats.Indexes.CreateOneAsync(new CreateIndexModel<Chat>(Builders<Chat>.IndexKeys.Ascending(x => x.DirectKey), new CreateIndexOptions { Unique = true, Sparse = true }), cancellationToken: stoppingToken);
                await db.Messages.Indexes.CreateOneAsync(new CreateIndexModel<Message>(Builders<Message>.IndexKeys.Ascending(x => x.ChatId).Ascending(x => x.SenderId).Ascending(x => x.ClientMessageId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await db.Messages.Indexes.CreateOneAsync(new CreateIndexModel<Message>(Builders<Message>.IndexKeys.Ascending(x => x.ChatId).Descending(x => x.SentAt)), cancellationToken: stoppingToken);
                await db.Messages.Indexes.CreateOneAsync(new CreateIndexModel<Message>(Builders<Message>.IndexKeys.Ascending(x => x.ChatId).Descending(x => x.SentAt).Descending(x => x.Id), new CreateIndexOptions { Name = "chat_message_timeline" }), cancellationToken: stoppingToken);
                await db.Follows.Indexes.CreateOneAsync(new CreateIndexModel<FollowEdge>(Builders<FollowEdge>.IndexKeys.Ascending(x => x.FollowerId).Ascending(x => x.FollowedId), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await db.Follows.Indexes.CreateOneAsync(new CreateIndexModel<FollowEdge>(Builders<FollowEdge>.IndexKeys.Ascending(x => x.FollowedId).Ascending(x => x.FollowerId)), cancellationToken: stoppingToken);
                await db.Chats.Indexes.CreateOneAsync(new CreateIndexModel<Chat>(Builders<Chat>.IndexKeys.Ascending(x => x.Participants).Descending(x => x.LastActivity)), cancellationToken: stoppingToken);
                await db.Chats.Indexes.CreateOneAsync(new CreateIndexModel<Chat>(Builders<Chat>.IndexKeys.Ascending(x => x.Participants).Descending(x => x.LastActivity).Descending(x => x.Id), new CreateIndexOptions { Name = "participant_activity_order" }), cancellationToken: stoppingToken);
                await db.Friends.Indexes.CreateOneAsync(new CreateIndexModel<Friend>(Builders<Friend>.IndexKeys.Ascending(x => x.UserId).Ascending(x => x.Status)), cancellationToken: stoppingToken);
                await db.Friends.Indexes.CreateOneAsync(new CreateIndexModel<Friend>(Builders<Friend>.IndexKeys.Ascending(x => x.FriendId).Ascending(x => x.Status)), cancellationToken: stoppingToken);
                await db.Feed.Indexes.CreateOneAsync(new CreateIndexModel<FeedItem>(Builders<FeedItem>.IndexKeys.Ascending(x => x.Key), new CreateIndexOptions { Unique = true, Sparse = true }), cancellationToken: stoppingToken);
                await db.Reactions.Indexes.CreateOneAsync(new CreateIndexModel<Reaction>(Builders<Reaction>.IndexKeys.Ascending(x => x.PostId).Ascending(x => x.FromIdentityUserId).Ascending(x => x.Emoji), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await db.History.Indexes.CreateOneAsync(new CreateIndexModel<HistoryEvent>(Builders<HistoryEvent>.IndexKeys.Ascending(x => x.IdentityUserId).Descending(x => x.PlayedAt)), cancellationToken: stoppingToken);
                await db.History.Indexes.CreateOneAsync(new CreateIndexModel<HistoryEvent>(Builders<HistoryEvent>.IndexKeys.Ascending(x => x.PlayedAt), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(90) }), cancellationToken: stoppingToken);
                await db.Notifications.Indexes.CreateOneAsync(new CreateIndexModel<Notification>(Builders<Notification>.IndexKeys.Ascending(x => x.TargetUserId).Descending(x => x.CreatedAt)), cancellationToken: stoppingToken);
                await EnsureNotificationTimeline(db, stoppingToken);
                await db.Notifications.Indexes.CreateOneAsync(new CreateIndexModel<Notification>(Builders<Notification>.IndexKeys.Ascending(x => x.Key), new CreateIndexOptions { Unique = true, Sparse = true }), cancellationToken: stoppingToken);
                await db.AvatarCleanup.Indexes.CreateOneAsync(new CreateIndexModel<AvatarCleanupIntent>(Builders<AvatarCleanupIntent>.IndexKeys.Ascending(x => x.Url), new CreateIndexOptions { Unique = true }), cancellationToken: stoppingToken);
                await db.AvatarCleanup.Indexes.CreateOneAsync(new CreateIndexModel<AvatarCleanupIntent>(Builders<AvatarCleanupIntent>.IndexKeys.Ascending(x => x.DueAt).Ascending(x => x.Attempts)), cancellationToken: stoppingToken);
                state.Ready = true;
                return;
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("User indexes unavailable on attempt {Attempt}.", attempt); if (attempt < 12) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        }
        throw new InvalidOperationException("User indexes could not initialize. Check local database readiness.");
    }
}
