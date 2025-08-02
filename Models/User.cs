using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace User.Models;

[BsonIgnoreExtraElements]
public class User
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    [BsonElement("IdentityUserId")]
    public string IdentityUserId { get; set; } = string.Empty;

    [BsonElement("UserName")]
    public string UserName { get; set; } = string.Empty;

    [BsonElement("DisplayName")]
    public string? DisplayName { get; set; }

    [BsonElement("Bio")]
    public string? Bio { get; set; }

    [BsonElement("AvatarUrl")]
    public string? AvatarUrl { get; set; }

    [BsonElement("IsPrivate")]
    public bool IsPrivate { get; set; } = false;

    [BsonElement("Playlists")]
    public List<PlaylistReference> Playlists { get; set; } = new();

    [BsonElement("FollowedUsers")]
    public List<UserReference> FollowedUsers { get; set; } = new();

    [BsonElement("Followers")]
    public List<UserReference> Followers { get; set; } = new();

    [BsonElement("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("UpdatedAt")]
    public DateTime? UpdatedAt { get; set; }
}

public class PlaylistReference
{
    [BsonElement("Id")]
    public string Id { get; set; } = string.Empty;
}

public class UserReference
{
    [BsonElement("Id")]
    public string Id { get; set; } = string.Empty;
} 