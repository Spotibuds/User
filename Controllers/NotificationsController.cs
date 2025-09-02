using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly MongoDbContext _context;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(MongoDbContext context, ILogger<NotificationsController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get all notifications for a user
    /// </summary>
    [HttpGet("{userId}")]
    public async Task<IActionResult> GetNotifications(string userId, [FromQuery] int limit = 50, [FromQuery] int skip = 0)
    {
        try
        {
            if (!_context.IsConnected || _context.Notifications == null)
            {
                return StatusCode(503, new { message = "Database temporarily unavailable" });
            }

            var notifications = await _context.Notifications
                .Find(n => n.TargetUserId == userId)
                .SortByDescending(n => n.CreatedAt)
                .Skip(skip)
                .Limit(limit)
                .ToListAsync();

            var totalCount = await _context.Notifications.CountDocumentsAsync(n => n.TargetUserId == userId);
            var unreadCount = await _context.Notifications.CountDocumentsAsync(
                n => n.TargetUserId == userId && n.Status == NotificationStatus.Unread);

            return Ok(new
            {
                notifications,
                totalCount,
                unreadCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get notifications for user {userId}");
            return StatusCode(500, new { message = "Failed to retrieve notifications" });
        }
    }

    /// <summary>
    /// Mark notification as read
    /// </summary>
    [HttpPost("{notificationId}/read")]
    public async Task<IActionResult> MarkAsRead(string notificationId, [FromBody] MarkNotificationDto dto)
    {
        try
        {
            if (!_context.IsConnected || _context.Notifications == null)
            {
                return StatusCode(503, new { message = "Database temporarily unavailable" });
            }

            var result = await _context.Notifications.UpdateOneAsync(
                n => n.Id == notificationId && n.TargetUserId == dto.UserId,
                Builders<Notification>.Update
                    .Set(n => n.Status, NotificationStatus.Read)
                    .Set(n => n.ReadAt, DateTime.UtcNow)
            );

            if (result.ModifiedCount == 0)
            {
                return NotFound(new { message = "Notification not found or not owned by user" });
            }

            return Ok(new { message = "Notification marked as read" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to mark notification as read: {notificationId}");
            return StatusCode(500, new { message = "Failed to update notification" });
        }
    }

    /// <summary>
    /// Mark notification as handled (e.g., friend request accepted/declined)
    /// </summary>
    [HttpPost("{notificationId}/handle")]
    public async Task<IActionResult> MarkAsHandled(string notificationId, [FromBody] MarkNotificationDto dto)
    {
        try
        {
            if (!_context.IsConnected || _context.Notifications == null)
            {
                return StatusCode(503, new { message = "Database temporarily unavailable" });
            }

            var result = await _context.Notifications.UpdateOneAsync(
                n => n.Id == notificationId && n.TargetUserId == dto.UserId,
                Builders<Notification>.Update
                    .Set(n => n.Status, NotificationStatus.Handled)
                    .Set(n => n.HandledAt, DateTime.UtcNow)
            );

            if (result.ModifiedCount == 0)
            {
                return NotFound(new { message = "Notification not found or not owned by user" });
            }

            return Ok(new { message = "Notification marked as handled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to mark notification as handled: {notificationId}");
            return StatusCode(500, new { message = "Failed to update notification" });
        }
    }

    /// <summary>
    /// Mark all notifications as read for a user
    /// </summary>
    [HttpPost("{userId}/read-all")]
    public async Task<IActionResult> MarkAllAsRead(string userId)
    {
        try
        {
            if (!_context.IsConnected || _context.Notifications == null)
            {
                return StatusCode(503, new { message = "Database temporarily unavailable" });
            }

            var result = await _context.Notifications.UpdateManyAsync(
                n => n.TargetUserId == userId && n.Status == NotificationStatus.Unread,
                Builders<Notification>.Update
                    .Set(n => n.Status, NotificationStatus.Read)
                    .Set(n => n.ReadAt, DateTime.UtcNow)
            );

            return Ok(new { message = $"{result.ModifiedCount} notifications marked as read" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to mark all notifications as read for user {userId}");
            return StatusCode(500, new { message = "Failed to update notifications" });
        }
    }

    /// <summary>
    /// Delete old handled notifications (cleanup)
    /// </summary>
    [HttpDelete("{userId}/cleanup")]
    public async Task<IActionResult> CleanupNotifications(string userId, [FromQuery] int daysOld = 30)
    {
        try
        {
            if (!_context.IsConnected || _context.Notifications == null)
            {
                return StatusCode(503, new { message = "Database temporarily unavailable" });
            }

            var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);
            
            var result = await _context.Notifications.DeleteManyAsync(
                n => n.TargetUserId == userId && 
                     n.Status == NotificationStatus.Handled && 
                     n.HandledAt < cutoffDate
            );

            return Ok(new { message = $"{result.DeletedCount} old notifications deleted" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to cleanup notifications for user {userId}");
            return StatusCode(500, new { message = "Failed to cleanup notifications" });
        }
    }
}

public class MarkNotificationDto
{
    public string UserId { get; set; } = string.Empty;
}
