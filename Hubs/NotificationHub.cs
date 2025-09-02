using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using User.Services;
using User.Entities;
using System.Security.Claims;
using System.Text.Json;

namespace User.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;
    private readonly INotificationService _notificationService;

    public NotificationHub(ILogger<NotificationHub> logger, INotificationService notificationService)
    {
        _logger = logger;
        _notificationService = notificationService;
    }

    public override async Task OnConnectedAsync()
    {
        try
        {
            var userId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning($"Unauthorized notification connection attempt: {Context.ConnectionId}");
                Context.Abort();
                return;
            }

            // Join user to their personal notification group
            await Groups.AddToGroupAsync(Context.ConnectionId, $"notifications_{userId}");
            
            // Send current unread count on connection
            var unreadCount = await _notificationService.GetUnreadCountAsync(userId);
            await Clients.Caller.SendAsync("UnreadCountUpdate", unreadCount);
            
            _logger.LogInformation($"User {userId} connected to notifications hub");
            await base.OnConnectedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in NotificationHub OnConnectedAsync");
            Context.Abort();
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserIdFromContext();
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"notifications_{userId}");
            _logger.LogInformation($"User {userId} disconnected from notifications hub");
        }
        
        await base.OnDisconnectedAsync(exception);
    }

    public async Task GetNotifications(int limit = 20, int skip = 0)
    {
        var userId = GetUserIdFromContext();
        if (string.IsNullOrEmpty(userId))
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized");
            return;
        }

        try
        {
            var notifications = await _notificationService.GetUserNotificationsAsync(userId, limit, skip);
            var unreadCount = await _notificationService.GetUnreadCountAsync(userId);
            
            await Clients.Caller.SendAsync("NotificationsLoaded", new
            {
                notifications,
                unreadCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error loading notifications for user {userId}");
            await Clients.Caller.SendAsync("Error", "Failed to load notifications");
        }
    }

    public async Task MarkAsRead(string notificationId)
    {
        var userId = GetUserIdFromContext();
        if (string.IsNullOrEmpty(userId))
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized");
            return;
        }

        try
        {
            await _notificationService.MarkAsReadAsync(notificationId, userId);
            
            // Send updated unread count
            var unreadCount = await _notificationService.GetUnreadCountAsync(userId);
            await Clients.Group($"notifications_{userId}").SendAsync("UnreadCountUpdate", unreadCount);
            
            await Clients.Caller.SendAsync("NotificationMarkedRead", notificationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error marking notification as read: {notificationId}");
            await Clients.Caller.SendAsync("Error", "Failed to mark notification as read");
        }
    }

    public async Task MarkAsHandled(string notificationId)
    {
        var userId = GetUserIdFromContext();
        if (string.IsNullOrEmpty(userId))
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized");
            return;
        }

        try
        {
            await _notificationService.MarkAsHandledAsync(notificationId, userId);
            
            // Send updated unread count
            var unreadCount = await _notificationService.GetUnreadCountAsync(userId);
            await Clients.Group($"notifications_{userId}").SendAsync("UnreadCountUpdate", unreadCount);
            
            await Clients.Caller.SendAsync("NotificationHandled", notificationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error marking notification as handled: {notificationId}");
            await Clients.Caller.SendAsync("Error", "Failed to handle notification");
        }
    }

    public async Task MarkAllAsRead()
    {
        var userId = GetUserIdFromContext();
        if (string.IsNullOrEmpty(userId))
        {
            await Clients.Caller.SendAsync("Error", "Unauthorized");
            return;
        }

        try
        {
            await _notificationService.MarkAllAsReadAsync(userId);
            
            // Send updated unread count (should be 0)
            await Clients.Group($"notifications_{userId}").SendAsync("UnreadCountUpdate", 0);
            
            await Clients.Caller.SendAsync("AllNotificationsMarkedRead");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error marking all notifications as read for user {userId}");
            await Clients.Caller.SendAsync("Error", "Failed to mark all notifications as read");
        }
    }

    // Method to be called by services to send real-time notifications
    public async Task SendNotificationToUser(string targetUserId, Notification notification)
    {
        try
        {
            _logger.LogInformation($"📢 Sending real-time notification to user {targetUserId}: {notification.Type}");
            
            // Send the new notification to the specific user's group
            await Clients.Group($"notifications_{targetUserId}").SendAsync("NewNotification", new
            {
                notification.Id,
                notification.Type,
                notification.Title,
                notification.Message,
                notification.TargetUserId,
                notification.SourceUserId,
                notification.Status,
                CreatedAt = notification.CreatedAt,
                notification.Data,
                notification.ActionUrl
            });
            
            // Also send updated unread count
            var unreadCount = await _notificationService.GetUnreadCountAsync(targetUserId);
            await Clients.Group($"notifications_{targetUserId}").SendAsync("UnreadCountUpdate", unreadCount);
            
            _logger.LogInformation($"✅ Successfully sent real-time notification to user {targetUserId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ Failed to send real-time notification to user {targetUserId}");
        }
    }

    private string? GetUserIdFromContext()
    {
        // Debug: Log all available claims
        _logger.LogInformation($"🔍 Debugging SignalR authentication for connection {Context.ConnectionId}:");
        _logger.LogInformation($"🔍 User.Identity.IsAuthenticated: {Context.User?.Identity?.IsAuthenticated}");
        _logger.LogInformation($"🔍 User.Identity.Name: {Context.User?.Identity?.Name}");
        _logger.LogInformation($"🔍 User.Identity.AuthenticationType: {Context.User?.Identity?.AuthenticationType}");
        
        if (Context.User?.Claims != null)
        {
            foreach (var claim in Context.User.Claims)
            {
                _logger.LogInformation($"🔍 Claim: {claim.Type} = {claim.Value}");
            }
        }

        // Try to get the user ID from the NameIdentifier claim (sub claim in JWT)
        var userIdClaim = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userIdClaim))
        {
            _logger.LogInformation($"✅ Found userId from NameIdentifier claim: {userIdClaim}");
            return userIdClaim;
        }

        // Fallback to the Name claim
        var nameIdentity = Context.User?.Identity?.Name;
        if (!string.IsNullOrEmpty(nameIdentity))
        {
            _logger.LogInformation($"✅ Found userId from Identity.Name: {nameIdentity}");
            return nameIdentity;
        }

        _logger.LogWarning($"❌ No userId found in any claim for connection {Context.ConnectionId}");
        return null;
    }
}
