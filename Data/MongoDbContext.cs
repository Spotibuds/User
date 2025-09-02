using MongoDB.Driver;
using User.Models;
using User.Entities;

namespace User.Data;

public class MongoDbContext
{
    private readonly IMongoDatabase? _database;
    private readonly bool _isConnected;

    public MongoDbContext(IMongoClient? mongoClient, string databaseName)
    {
        if (mongoClient != null)
        {
            try
            {
                _database = mongoClient.GetDatabase(databaseName);
                _isConnected = true;
            }
            catch (Exception ex)
            {
                _database = null;
                _isConnected = false;
            }
        }
        else
        {
            _database = null;
            _isConnected = false;
        }
    }

    public bool IsConnected => _isConnected && _database != null;

    public IMongoCollection<Models.User>? Users => _database?.GetCollection<Models.User>("users");
    public IMongoCollection<Friend>? Friends => _database?.GetCollection<Friend>("friends");
    public IMongoCollection<Chat>? Chats => _database?.GetCollection<Chat>("chats");
    public IMongoCollection<Message>? Messages => _database?.GetCollection<Message>("messages");
    public IMongoCollection<User.Entities.Reaction>? Reactions => _database?.GetCollection<User.Entities.Reaction>("reactions");
    public IMongoCollection<User.Entities.FeedItem>? Feed => _database?.GetCollection<User.Entities.FeedItem>("feed");
    public IMongoCollection<Notification>? Notifications => _database?.GetCollection<Notification>("notifications");
} 