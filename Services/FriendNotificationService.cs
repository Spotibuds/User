using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Hubs;
using User.Entities;

namespace User.Services;

public class FriendNotificationService : IFriendNotificationService
{
    private readonly IHubContext<FriendHub> _friendHub;
    private readonly ILogger<FriendNotificationService> _logger;
    private readonly MongoDbContext _context;
    private readonly IRabbitMqEventService _eventService;
    private readonly INotificationService _notificationService;

    public FriendNotificationService(
        IHubContext<FriendHub> friendHub, 
        ILogger<FriendNotificationService> logger, 
        MongoDbContext context,
        IRabbitMqEventService eventService,
        INotificationService notificationService)
    {
        _friendHub = friendHub;
        _logger = logger;
        _context = context;
        _eventService = eventService;
        _notificationService = notificationService;
    }

    public async Task NotifyFriendRequestSent(string userId, string targetUserId, string requesterUsername)
    {
        try
        {
            // Get user details from database
            var requester = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            var target = await _context.Users.Find(u => u.IdentityUserId == targetUserId).FirstOrDefaultAsync();
            
            if (requester == null || target == null)
            {
                _logger.LogError($"Could not find users for notification: requester={userId}, target={targetUserId}");
                return;
            }

            // Find the friendship record to get the request ID
            var friendship = await _context.Friends
                .Find(f => f.UserId == requester.Id && f.FriendId == target.Id && f.Status == Entities.FriendStatus.Pending)
                .SortByDescending(f => f.CreatedAt)
                .FirstOrDefaultAsync();
                
            if (friendship == null)
            {
                _logger.LogError($"Could not find friendship record for notification");
                return;
            }

            // Create persistent notification
            await _notificationService.CreateNotificationAsync(
                targetUserId,
                NotificationType.FriendRequest,
                "Friend Request",
                $"{requester.UserName} sent you a friend request",
                userId,
                new Dictionary<string, object> 
                { 
                    { "requestId", friendship.Id },
                    { "senderName", requester.UserName ?? "Unknown User" },
                    { "senderAvatar", requester.AvatarUrl ?? "" }
                },
                $"/friends"
            );

            // Publish to RabbitMQ for cross-service events
            await _eventService.PublishFriendEventAsync("request", new FriendEvent
            {
                EventType = "friend_request",
                TargetUserId = targetUserId,
                SourceUserId = userId,
                SourceUserName = requester.UserName ?? "Unknown User",
                SourceUserAvatar = requester.AvatarUrl,
                FriendshipId = friendship.Id,
                Data = new Dictionary<string, object> { { "requestId", friendship.Id } }
            });

            // Send real-time SignalR notification
            await _friendHub.Clients.Group($"user_{targetUserId}").SendAsync("FriendRequestReceived", new
            {
                RequestId = friendship.Id,
                SenderId = userId, // Use IdentityUserId for frontend
                SenderName = requester.UserName ?? "Unknown User",
                SenderAvatar = requester.AvatarUrl,
                Timestamp = friendship.CreatedAt.ToString("O") // ISO 8601 format
            });

            // Notify sender about successful send
            await _friendHub.Clients.Group($"user_{userId}").SendAsync("FriendRequestSent", new
            {
                RequestId = friendship.Id,
                TargetUserId = targetUserId, // Use IdentityUserId for frontend
                Timestamp = friendship.CreatedAt.ToString("O") // ISO 8601 format
            });

            _logger.LogInformation($"Friend request notifications sent from {requesterUsername} to user {targetUserId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send friend request notification");
        }
    }

    public async Task NotifyFriendRequestAccepted(string userId, string friendId, string accepterUsername)
    {
        try
        {
            // Get user details from database
            var accepter = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            var friend = await _context.Users.Find(u => u.IdentityUserId == friendId).FirstOrDefaultAsync();
            
            if (accepter == null || friend == null)
            {
                _logger.LogError($"Could not find users for accepted notification: accepter={userId}, friend={friendId}");
                return;
            }

            // Find the friendship record to get the request ID
            var friendship = await _context.Friends
                .Find(f => ((f.UserId == accepter.Id && f.FriendId == friend.Id) || 
                           (f.UserId == friend.Id && f.FriendId == accepter.Id)) && 
                           f.Status == Entities.FriendStatus.Accepted)
                .SortByDescending(f => f.CreatedAt)
                .FirstOrDefaultAsync();

            // Create persistent notification
            await _notificationService.CreateNotificationAsync(
                friendId,
                NotificationType.FriendRequestAccepted,
                "Friend Request Accepted",
                $"{accepter.UserName} accepted your friend request",
                userId,
                new Dictionary<string, object> 
                { 
                    { "requestId", friendship?.Id ?? "unknown" },
                    { "friendId", userId },
                    { "friendName", accepter.UserName ?? "Unknown User" },
                    { "friendAvatar", accepter.AvatarUrl ?? "" }
                },
                $"/friends"
            );

            // Mark the original friend request notification as handled
            if (friendship != null)
            {
                var originalNotification = await _context.Notifications
                    .Find(n => n.TargetUserId == userId && 
                              n.Type == NotificationType.FriendRequest &&
                              n.Data.ContainsKey("requestId") &&
                              n.Data["requestId"].ToString() == friendship.Id)
                    .FirstOrDefaultAsync();

                if (originalNotification != null)
                {
                    await _notificationService.MarkAsHandledAsync(originalNotification.Id, userId);
                }
            }

            // Publish to RabbitMQ for cross-service events
            await _eventService.PublishFriendEventAsync("accepted", new FriendEvent
            {
                EventType = "friend_accepted",
                TargetUserId = friendId,
                SourceUserId = userId,
                SourceUserName = accepter.UserName ?? "Unknown User",
                SourceUserAvatar = accepter.AvatarUrl,
                FriendshipId = friendship?.Id,
                Data = new Dictionary<string, object> 
                { 
                    { "requestId", friendship?.Id ?? "unknown" },
                    { "friendId", userId }
                }
            });

            // Send real-time SignalR notification
            await _friendHub.Clients.Group($"user_{friendId}").SendAsync("FriendRequestAccepted", new
            {
                requestId = friendship?.Id ?? "unknown", // Match frontend expectation (lowercase)
                friendId = userId, // The person who accepted (becomes their new friend)
                friendName = accepter.UserName ?? "Unknown User",
                friendAvatar = accepter.AvatarUrl,
                timestamp = DateTime.UtcNow.ToString("O")
            });

            _logger.LogInformation($"Friend request accepted notifications sent from {accepterUsername} to user {friendId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send friend request accepted notification");
        }
    }

    public async Task NotifyFriendRequestDeclined(string userId, string friendId, string declinerUsername)
    {
        try
        {
            // Get user details from database
            var decliner = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            var friend = await _context.Users.Find(u => u.IdentityUserId == friendId).FirstOrDefaultAsync();
            
            if (decliner == null || friend == null)
            {
                _logger.LogError($"Could not find users for declined notification: decliner={userId}, friend={friendId}");
                return;
            }

            // Find the declined friendship record
            var friendship = await _context.Friends
                .Find(f => ((f.UserId == decliner.Id && f.FriendId == friend.Id) || 
                           (f.UserId == friend.Id && f.FriendId == decliner.Id)) && 
                           f.Status == Entities.FriendStatus.Declined)
                .SortByDescending(f => f.CreatedAt)
                .FirstOrDefaultAsync();

            // Create persistent notification (optional - user might not want to see declination)
            await _notificationService.CreateNotificationAsync(
                friendId,
                NotificationType.FriendRequestDeclined,
                "Friend Request Declined",
                $"{decliner.UserName} declined your friend request",
                userId,
                new Dictionary<string, object> 
                { 
                    { "requestId", friendship?.Id ?? "unknown" },
                    { "declinedBy", userId }
                },
                $"/friends"
            );

            // Mark the original friend request notification as handled
            if (friendship != null)
            {
                var originalNotification = await _context.Notifications
                    .Find(n => n.TargetUserId == userId && 
                              n.Type == NotificationType.FriendRequest &&
                              n.Data.ContainsKey("requestId") &&
                              n.Data["requestId"].ToString() == friendship.Id)
                    .FirstOrDefaultAsync();

                if (originalNotification != null)
                {
                    await _notificationService.MarkAsHandledAsync(originalNotification.Id, userId);
                }
            }

            // Publish to RabbitMQ for cross-service events
            await _eventService.PublishFriendEventAsync("declined", new FriendEvent
            {
                EventType = "friend_declined",
                TargetUserId = friendId,
                SourceUserId = userId,
                SourceUserName = decliner.UserName ?? "Unknown User",
                SourceUserAvatar = decliner.AvatarUrl,
                FriendshipId = friendship?.Id,
                Data = new Dictionary<string, object> 
                { 
                    { "requestId", friendship?.Id ?? "unknown" },
                    { "declinedBy", userId }
                }
            });

            // Send real-time SignalR notification
            await _friendHub.Clients.Group($"user_{friendId}").SendAsync("FriendRequestDeclined", new
            {
                RequestId = friendship?.Id ?? "declined",
                DeclinedBy = userId,
                Timestamp = DateTime.UtcNow.ToString("O")
            });

            _logger.LogInformation($"Friend request declined notifications sent from {declinerUsername} to user {friendId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send friend request declined notification");
        }
    }

    public async Task NotifyFriendRemoved(string userId, string friendId, string removerUsername)
    {
        try
        {
            // Get user details from database
            var remover = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            var friend = await _context.Users.Find(u => u.IdentityUserId == friendId).FirstOrDefaultAsync();
            
            if (remover == null || friend == null)
            {
                _logger.LogError($"Could not find users for removal notification: remover={userId}, friend={friendId}");
                return;
            }

            // Create persistent notification
            await _notificationService.CreateNotificationAsync(
                friendId,
                NotificationType.FriendRemoved,
                "Friend Removed",
                $"{remover.UserName} removed you from their friends",
                userId,
                new Dictionary<string, object> 
                { 
                    { "removedBy", userId },
                    { "removerName", remover.UserName ?? "Unknown User" }
                },
                $"/friends"
            );

            // Publish to RabbitMQ for cross-service events
            await _eventService.PublishFriendEventAsync("removed", new FriendEvent
            {
                EventType = "friend_removed",
                TargetUserId = friendId,
                SourceUserId = userId,
                SourceUserName = remover.UserName ?? "Unknown User",
                SourceUserAvatar = remover.AvatarUrl,
                Data = new Dictionary<string, object> 
                { 
                    { "removedBy", userId }
                }
            });

            // Send real-time SignalR notification
            await _friendHub.Clients.Group($"user_{friendId}").SendAsync("FriendRemoved", new
            {
                RemovedBy = userId,
                RemoverName = remover.UserName ?? "Unknown User",
                Timestamp = DateTime.UtcNow.ToString("O")
            });

            _logger.LogInformation($"Friend removed notifications sent from {removerUsername} to user {friendId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send friend removed notification");
        }
    }

    public async Task NotifyFriendRemovedByYou(string userId, string removedFriendId, string removedUsername)
    {
        try
        {
            // This is just a confirmation notification, no need for persistent storage
            // Just log it for debugging
            _logger.LogInformation($"Friend {removedUsername} (ID: {removedFriendId}) was removed by user {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log friend removed by you event");
        }
    }
}