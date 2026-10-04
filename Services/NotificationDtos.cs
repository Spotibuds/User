using User.Entities;
namespace User.Services;

public sealed record NotificationDto(string Id, string Type, string Status, string Title, string Message, string TargetUserId, string? SourceUserId, DateTime CreatedAt, Dictionary<string, object> Data, string? ActionUrl, DateTime? ReadAt, DateTime? HandledAt, DateTime? ExpiresAt, DateTime? DismissedAt)
{
    public static NotificationDto From(Notification value, string? actionUrl = null) => new(value.Id, value.Type.ToString(), value.Status.ToString(), value.Title, value.Message, value.TargetUserId, value.SourceUserId, value.CreatedAt, value.Data, actionUrl ?? value.ActionUrl, value.ReadAt, value.HandledAt, value.ExpiresAt, value.DismissedAt);
}
public sealed record NotificationSnapshot(List<NotificationDto> Notifications, long TotalCount, int UnreadCount, int Limit, int Skip, string? NextBefore);
public sealed record NotificationAcknowledgement(string Message, NotificationDto? Notification = null, long? TotalCount = null, int? UnreadCount = null, string? ThroughId = null, bool SynchronizationPending = false);
public sealed class NotificationBoundary { public string? ThroughId { get; set; } }
