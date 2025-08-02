using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace User.Entities;

public class Chat : BaseEntity
{
    [BsonElement("participants")]
    public List<string> Participants { get; set; } = new();

    [BsonElement("isGroup")]
    public bool IsGroup { get; set; } = false;

    [BsonElement("name")]
    public string? Name { get; set; }

    [BsonElement("lastMessageId")]
    public string? LastMessageId { get; set; }

    [BsonElement("lastActivity")]
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
} 