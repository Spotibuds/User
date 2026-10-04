using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace User.Entities;

public class Friend : BaseEntity
{
    public string PairKey { get; set; } = string.Empty;
    // A new public identity for each request attempt; the unique pair row is retained.
    // Legacy records use their original Id until a new attempt is created.
    [BsonIgnoreIfNull]
    public string? RequestId { get; set; }
    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("friendId")]
    public string FriendId { get; set; } = string.Empty;

    [BsonElement("status")]
    public FriendStatus Status { get; set; } = FriendStatus.Pending;

    [BsonElement("requestedAt")]
    public new DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("acceptedAt")]
    public DateTime? AcceptedAt { get; set; }

    [BsonIgnoreIfNull]
    public DateTime? RespondedAt { get; set; }
}

public enum FriendStatus
{
    Pending = 0,
    Accepted = 1,
    Blocked = 2,
    Declined = 3,
    Cancelled = 4,
    Removed = 5
}
