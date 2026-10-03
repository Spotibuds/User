using MongoDB.Driver;
using User.Entities;
namespace User.Data;
public class MongoDbContext(IMongoClient client, string databaseName)
{
    public IMongoDatabase Database { get; } = client.GetDatabase(databaseName);
    public bool IsConnected => true; // Availability belongs to readiness and operations, never client construction.
    public IMongoCollection<Models.User> Users => Database.GetCollection<Models.User>("users");
    public IMongoCollection<Friend> Friends => Database.GetCollection<Friend>("friends");
    public IMongoCollection<Chat> Chats => Database.GetCollection<Chat>("chats");
    public IMongoCollection<Message> Messages => Database.GetCollection<Message>("messages");
    public IMongoCollection<Reaction> Reactions => Database.GetCollection<Reaction>("reactions");
    public IMongoCollection<FeedItem> Feed => Database.GetCollection<FeedItem>("feed");
    public IMongoCollection<Notification> Notifications => Database.GetCollection<Notification>("notifications");
    public IMongoCollection<HistoryEvent> History => Database.GetCollection<HistoryEvent>("history");
    public IMongoCollection<FollowEdge> Follows => Database.GetCollection<FollowEdge>("follows");
    public IMongoCollection<AvatarCleanupIntent> AvatarCleanup => Database.GetCollection<AvatarCleanupIntent>("avatar_cleanup");
}
