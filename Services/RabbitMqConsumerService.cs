using Microsoft.AspNetCore.SignalR;
using User.Hubs;
using User.Services;
using System.Text.Json;

namespace User.Services;

public class RabbitMqConsumerService : BackgroundService
{
    private readonly IRabbitMqService _rabbitMqService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RabbitMqConsumerService> _logger;

    public RabbitMqConsumerService(
        IRabbitMqService rabbitMqService,
        IServiceProvider serviceProvider,
        ILogger<RabbitMqConsumerService> logger)
    {
        _rabbitMqService = rabbitMqService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_rabbitMqService == null)
        {
            _logger.LogWarning("RabbitMQ service not available, skipping consumer setup");
            return Task.CompletedTask;
        }

        try
        {
            // TEMPORARILY DISABLED: Start consuming friend events to debug duplicate notifications
            // _rabbitMqService.StartConsumingAsync("spotibuds.notifications", HandleFriendEventMessage);
            _logger.LogInformation("RabbitMQ consumer temporarily disabled for friend events (debugging duplicates)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start RabbitMQ consumer");
        }

        return Task.CompletedTask;
    }

    private async Task HandleFriendEventMessage(string message)
    {
        try
        {
            _logger.LogInformation($"Received RabbitMQ message: {message}");

            var friendEvent = JsonSerializer.Deserialize<FriendEvent>(message);
            if (friendEvent == null)
            {
                _logger.LogWarning("Failed to deserialize friend event message");
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<NotificationHub>>();
            var friendHubContext = scope.ServiceProvider.GetRequiredService<IHubContext<FriendHub>>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

            // Send real-time notifications based on event type
            switch (friendEvent.EventType)
            {
                case "friend_request":
                    await HandleFriendRequestEvent(friendEvent, hubContext, notificationService);
                    break;

                case "friend_accepted":
                    await HandleFriendAcceptedEvent(friendEvent, hubContext, friendHubContext, notificationService);
                    break;

                case "friend_declined":
                    await HandleFriendDeclinedEvent(friendEvent, hubContext, friendHubContext, notificationService);
                    break;

                case "friend_removed":
                    await HandleFriendRemovedEvent(friendEvent, hubContext, friendHubContext, notificationService);
                    break;

                default:
                    _logger.LogWarning($"Unknown friend event type: {friendEvent.EventType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing friend event message: {message}");
        }
    }

    private async Task HandleFriendRequestEvent(
        FriendEvent friendEvent, 
        IHubContext<NotificationHub> hubContext, 
        INotificationService notificationService)
    {
        // Send real-time notification
        await hubContext.Clients.Group($"notifications_{friendEvent.TargetUserId}")
            .SendAsync("NewNotification", new
            {
                type = "friend_request",
                title = "Friend Request",
                message = $"{friendEvent.SourceUserName} sent you a friend request",
                sourceUserId = friendEvent.SourceUserId,
                sourceUserName = friendEvent.SourceUserName,
                sourceUserAvatar = friendEvent.SourceUserAvatar,
                data = friendEvent.Data,
                timestamp = friendEvent.Timestamp.ToString("O")
            });

        // Update unread count
        var unreadCount = await notificationService.GetUnreadCountAsync(friendEvent.TargetUserId);
        await hubContext.Clients.Group($"notifications_{friendEvent.TargetUserId}")
            .SendAsync("UnreadCountUpdate", unreadCount);
    }

    private async Task HandleFriendAcceptedEvent(
        FriendEvent friendEvent, 
        IHubContext<NotificationHub> hubContext, 
        IHubContext<FriendHub> friendHubContext,
        INotificationService notificationService)
    {
        // Send notification update
        await hubContext.Clients.Group($"notifications_{friendEvent.TargetUserId}")
            .SendAsync("NewNotification", new
            {
                type = "friend_accepted",
                title = "Friend Request Accepted",
                message = $"{friendEvent.SourceUserName} accepted your friend request",
                sourceUserId = friendEvent.SourceUserId,
                sourceUserName = friendEvent.SourceUserName,
                sourceUserAvatar = friendEvent.SourceUserAvatar,
                data = friendEvent.Data,
                timestamp = friendEvent.Timestamp.ToString("O")
            });

        // Also send real-time friend list update via FriendHub
        await friendHubContext.Clients.Group($"user_{friendEvent.TargetUserId}")
            .SendAsync("FriendListUpdated");

        // Update unread count
        var unreadCount = await notificationService.GetUnreadCountAsync(friendEvent.TargetUserId);
        await hubContext.Clients.Group($"notifications_{friendEvent.TargetUserId}")
            .SendAsync("UnreadCountUpdate", unreadCount);
    }

    private async Task HandleFriendDeclinedEvent(
        FriendEvent friendEvent, 
        IHubContext<NotificationHub> hubContext, 
        IHubContext<FriendHub> friendHubContext,
        INotificationService notificationService)
    {
        // Send notification (user might not want to see declines, make this optional)
        await hubContext.Clients.Group($"notifications_{friendEvent.TargetUserId}")
            .SendAsync("NewNotification", new
            {
                type = "friend_declined",
                title = "Friend Request Declined",
                message = $"{friendEvent.SourceUserName} declined your friend request",
                sourceUserId = friendEvent.SourceUserId,
                sourceUserName = friendEvent.SourceUserName,
                sourceUserAvatar = friendEvent.SourceUserAvatar,
                data = friendEvent.Data,
                timestamp = friendEvent.Timestamp.ToString("O")
            });

        // Update unread count
        var unreadCount = await notificationService.GetUnreadCountAsync(friendEvent.TargetUserId);
        await hubContext.Clients.Group($"notifications_{friendEvent.TargetUserId}")
            .SendAsync("UnreadCountUpdate", unreadCount);
    }

    private async Task HandleFriendRemovedEvent(
        FriendEvent friendEvent, 
        IHubContext<NotificationHub> hubContext, 
        IHubContext<FriendHub> friendHubContext,
        INotificationService notificationService)
    {
        // Send notification
        await hubContext.Clients.Group($"notifications_{friendEvent.TargetUserId}")
            .SendAsync("NewNotification", new
            {
                type = "friend_removed",
                title = "Friend Removed",
                message = $"{friendEvent.SourceUserName} removed you from their friends",
                sourceUserId = friendEvent.SourceUserId,
                sourceUserName = friendEvent.SourceUserName,
                sourceUserAvatar = friendEvent.SourceUserAvatar,
                data = friendEvent.Data,
                timestamp = friendEvent.Timestamp.ToString("O")
            });

        // Also send real-time friend list update via FriendHub
        await friendHubContext.Clients.Group($"user_{friendEvent.TargetUserId}")
            .SendAsync("FriendListUpdated");

        // Update unread count
        var unreadCount = await notificationService.GetUnreadCountAsync(friendEvent.TargetUserId);
        await hubContext.Clients.Group($"notifications_{friendEvent.TargetUserId}")
            .SendAsync("UnreadCountUpdate", unreadCount);
    }
}
