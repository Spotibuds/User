using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace User.Entities;

public class Message : BaseEntity
{
    [BsonElement("chatId")]
    public string ChatId { get; set; } = string.Empty;

    [BsonElement("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [BsonElement("content")]
    public string Content { get; set; } = string.Empty;

    [BsonElement("messageType")]
    public MessageType Type { get; set; } = MessageType.Text;

    [BsonElement("sentAt")]
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    [BsonElement("isEdited")]
    public bool IsEdited { get; set; } = false;

    [BsonElement("editedAt")]
    public DateTime? EditedAt { get; set; }

    [BsonElement("readBy")]
    public List<MessageRead> ReadBy { get; set; } = new();

    [BsonElement("replyToId")]
    public string? ReplyToId { get; set; }
}

public class MessageRead
{
    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("readAt")]
    public DateTime ReadAt { get; set; } = DateTime.UtcNow;
}

public enum MessageType
{
    Text = 0,
    Image = 1,
    Audio = 2,
    File = 3,
    System = 4
} 