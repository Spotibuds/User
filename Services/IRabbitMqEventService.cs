namespace User.Services;

public interface IRabbitMqEventService
{
    Task PublishFriendEventAsync(string eventType, object eventData);
}

public class FriendEvent
{
    public string EventType { get; set; } = string.Empty; // "friend_request", "friend_accepted", etc.
    public string TargetUserId { get; set; } = string.Empty; // Who should receive the notification
    public string SourceUserId { get; set; } = string.Empty; // Who triggered the action
    public string SourceUserName { get; set; } = string.Empty;
    public string? SourceUserAvatar { get; set; }
    public string? FriendshipId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> Data { get; set; } = new();
}
