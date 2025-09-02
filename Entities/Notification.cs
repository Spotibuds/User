using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace User.Entities;

public enum NotificationType
{
    FriendRequest,
    FriendRequestAccepted,
    FriendRequestDeclined,
    FriendRemoved,
    Message,
    Other
}

public enum NotificationStatus
{
    Unread,
    Read,
    Handled
}

public class Notification : BaseEntity
{
    /// <summary>
    /// The user who will receive this notification
    /// </summary>
    public string TargetUserId { get; set; } = string.Empty; // IdentityUserId
    
    /// <summary>
    /// The user who triggered this notification (if applicable)
    /// </summary>
    public string? SourceUserId { get; set; } // IdentityUserId
    
    public NotificationType Type { get; set; }
    
    public NotificationStatus Status { get; set; } = NotificationStatus.Unread;
    
    public string Title { get; set; } = string.Empty;
    
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// Additional data for the notification (JSON object)
    /// e.g., {"requestId": "123", "friendId": "456"}
    /// </summary>
    public Dictionary<string, object> Data { get; set; } = new();
    
    /// <summary>
    /// Action URL or page to navigate to when notification is clicked
    /// </summary>
    public string? ActionUrl { get; set; }
    
    /// <summary>
    /// When the notification should expire (auto-delete)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
    
    public new DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public DateTime? ReadAt { get; set; }
    
    public DateTime? HandledAt { get; set; }
}
