using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace User.Models;

public class User
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    [BsonElement("identityUserId")]
    public string IdentityUserId { get; set; } = string.Empty;

    [BsonElement("userName")]
    public string UserName { get; set; } = string.Empty;

    [BsonElement("isPrivate")]
    public bool IsPrivate { get; set; } = false;

    [BsonElement("playlists")]
    public List<PlaylistReference> Playlists { get; set; } = new();

    [BsonElement("followedUsers")]
    public List<UserReference> FollowedUsers { get; set; } = new();

    [BsonElement("followers")]
    public List<UserReference> Followers { get; set; } = new();

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}

public class PlaylistReference
{
    [BsonElement("id")]
    public string Id { get; set; } = string.Empty;
}

public class UserReference
{
    [BsonElement("id")]
    public string Id { get; set; } = string.Empty;
} 