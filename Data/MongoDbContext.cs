using MongoDB.Driver;
using User.Models;

namespace User.Data;

public class MongoDbContext
{
    private readonly IMongoDatabase _database;

    public MongoDbContext(IMongoClient mongoClient, string databaseName)
    {
        _database = mongoClient.GetDatabase(databaseName);
    }

    public IMongoCollection<Models.User> Users => _database.GetCollection<Models.User>("users");
} 