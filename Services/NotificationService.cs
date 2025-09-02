using MongoDB.Driver;
using User.Data;
using User.Entities;

namespace User.Services;

public interface INotificationService
{
    Task CreateNotificationAsync(string targetUserId, NotificationType type, string title, string message, 
        string? sourceUserId = null, Dictionary<string, object>? data = null, string? actionUrl = null);
    Task<List<Notification>> GetUserNotificationsAsync(string userId, int limit = 50, int skip = 0);
    Task<int> GetUnreadCountAsync(string userId);
    Task MarkAsReadAsync(string notificationId, string userId);
    Task MarkAsHandledAsync(string notificationId, string userId);
    Task MarkAllAsReadAsync(string userId);
    Task CleanupOldNotificationsAsync(string userId, int daysOld = 30);
}

public class NotificationService : INotificationService
{
    private readonly MongoDbContext _context;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(MongoDbContext context, ILogger<NotificationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task CreateNotificationAsync(string targetUserId, NotificationType type, string title, string message, 
        string? sourceUserId = null, Dictionary<string, object>? data = null, string? actionUrl = null)
    {
        try
        {
            if (!_context.IsConnected || _context.Notifications == null)
            {
                _logger.LogWarning("Cannot create notification - database not connected");
                return;
            }

            var notification = new Notification
            {
                TargetUserId = targetUserId,
                SourceUserId = sourceUserId,
                Type = type,
                Title = title,
                Message = message,
                Data = data ?? new Dictionary<string, object>(),
                ActionUrl = actionUrl,
                Status = NotificationStatus.Unread,
                CreatedAt = DateTime.UtcNow,
                // Auto-expire friend request notifications after 30 days
                ExpiresAt = type == NotificationType.FriendRequest ? DateTime.UtcNow.AddDays(30) : null
            };

            await _context.Notifications.InsertOneAsync(notification);
            
            _logger.LogInformation($"Created notification for user {targetUserId}: {type}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to create notification for user {targetUserId}");
        }
    }

    public async Task<List<Notification>> GetUserNotificationsAsync(string userId, int limit = 50, int skip = 0)
    {
        if (!_context.IsConnected || _context.Notifications == null)
        {
            return new List<Notification>();
        }

        try
        {
            return await _context.Notifications
                .Find(n => n.TargetUserId == userId && 
                          (n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow))
                .SortByDescending(n => n.CreatedAt)
                .Skip(skip)
                .Limit(limit)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get notifications for user {userId}");
            return new List<Notification>();
        }
    }

    public async Task<int> GetUnreadCountAsync(string userId)
    {
        if (!_context.IsConnected || _context.Notifications == null)
        {
            return 0;
        }

        try
        {
            return (int)await _context.Notifications.CountDocumentsAsync(
                n => n.TargetUserId == userId && 
                     n.Status == NotificationStatus.Unread &&
                     (n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get unread count for user {userId}");
            return 0;
        }
    }

    public async Task MarkAsReadAsync(string notificationId, string userId)
    {
        if (!_context.IsConnected || _context.Notifications == null)
        {
            return;
        }

        try
        {
            await _context.Notifications.UpdateOneAsync(
                n => n.Id == notificationId && n.TargetUserId == userId,
                Builders<Notification>.Update
                    .Set(n => n.Status, NotificationStatus.Read)
                    .Set(n => n.ReadAt, DateTime.UtcNow)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to mark notification as read: {notificationId}");
        }
    }

    public async Task MarkAsHandledAsync(string notificationId, string userId)
    {
        if (!_context.IsConnected || _context.Notifications == null)
        {
            return;
        }

        try
        {
            await _context.Notifications.UpdateOneAsync(
                n => n.Id == notificationId && n.TargetUserId == userId,
                Builders<Notification>.Update
                    .Set(n => n.Status, NotificationStatus.Handled)
                    .Set(n => n.HandledAt, DateTime.UtcNow)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to mark notification as handled: {notificationId}");
        }
    }

    public async Task MarkAllAsReadAsync(string userId)
    {
        if (!_context.IsConnected || _context.Notifications == null)
        {
            return;
        }

        try
        {
            await _context.Notifications.UpdateManyAsync(
                n => n.TargetUserId == userId && n.Status == NotificationStatus.Unread,
                Builders<Notification>.Update
                    .Set(n => n.Status, NotificationStatus.Read)
                    .Set(n => n.ReadAt, DateTime.UtcNow)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to mark all notifications as read for user {userId}");
        }
    }

    public async Task CleanupOldNotificationsAsync(string userId, int daysOld = 30)
    {
        if (!_context.IsConnected || _context.Notifications == null)
        {
            return;
        }

        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);
            
            await _context.Notifications.DeleteManyAsync(
                n => n.TargetUserId == userId && 
                     n.Status == NotificationStatus.Handled && 
                     n.HandledAt < cutoffDate
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to cleanup old notifications for user {userId}");
        }
    }
}
