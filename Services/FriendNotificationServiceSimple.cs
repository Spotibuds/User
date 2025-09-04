using Microsoft.AspNetCore.SignalR;
using User.Hubs;
using User.Entities;

namespace User.Services;

public class FriendNotificationServiceSimple : IFriendNotificationService
{
    private readonly IHubContext<NotificationHub> _notificationHub;
    private readonly IHubContext<FriendHub> _friendHub;
    private readonly INotificationService _notificationService;
    private readonly ILogger<FriendNotificationServiceSimple> _logger;

    public FriendNotificationServiceSimple(
        IHubContext<NotificationHub> notificationHub,
        IHubContext<FriendHub> friendHub,
        INotificationService notificationService,
        ILogger<FriendNotificationServiceSimple> logger)
    {
        _notificationHub = notificationHub;
        _friendHub = friendHub;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task NotifyFriendRequestSent(string userId, string targetUserId, string requesterUsername, string friendshipId)
    {
        try
        {
            _logger.LogInformation($"📢 Creating persistent friend request notification from {requesterUsername} to {targetUserId} (FriendshipId: {friendshipId})");
            
            // First, create persistent notification in database
            await _notificationService.CreateNotificationAsync(
                targetUserId: targetUserId,
                type: NotificationType.FriendRequest,
                title: "New Friend Request",
                message: $"{requesterUsername} sent you a friend request",
                sourceUserId: userId,
                data: new Dictionary<string, object>
                {
                    { "fromUserId", userId },
                    { "fromUsername", requesterUsername },
                    { "requestId", friendshipId },
                    { "friendshipId", friendshipId }
                }
            );
            
            // Then send real-time SignalR notification 
            await _notificationHub.Clients.Group($"notifications_{targetUserId}")
                .SendAsync("NewNotification", new { 
                    Type = "FriendRequest",
                    Title = "New Friend Request",
                    Message = $"{requesterUsername} sent you a friend request",
                    SourceUserId = userId,
                    TargetUserId = targetUserId,
                    Data = new Dictionary<string, object>
                    {
                        { "fromUserId", userId },
                        { "fromUsername", requesterUsername },
                        { "requestId", friendshipId }, // Put requestId inside data for frontend access
                        { "friendshipId", friendshipId }
                    },
                    Timestamp = DateTime.UtcNow.ToString("O")
                });
                
            _logger.LogInformation($"✅ Successfully created persistent notification and sent SignalR notification to {targetUserId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend request notification");
        }
    }

    public async Task NotifyFriendRequestAccepted(string userId, string friendId, string accepterUsername)
    {
        try
        {
            _logger.LogInformation($"📢 Creating persistent friend request accepted notification from {accepterUsername} to {userId}");
            
            // First, create persistent notification in database
            await _notificationService.CreateNotificationAsync(
                targetUserId: userId,
                type: NotificationType.FriendRequestAccepted,
                title: "Friend Request Accepted",
                message: $"{accepterUsername} accepted your friend request",
                sourceUserId: friendId,
                data: new Dictionary<string, object>
                {
                    { "friendId", friendId },
                    { "accepterUsername", accepterUsername }
                }
            );
            
            // Then send real-time SignalR notification via NotificationHub
            await _notificationHub.Clients.Group($"notifications_{userId}")
                .SendAsync("NewNotification", new { 
                    Type = "FriendRequestAccepted",
                    Title = "Friend Request Accepted",
                    Message = $"{accepterUsername} accepted your friend request",
                    SourceUserId = friendId,
                    TargetUserId = userId,
                    Data = new Dictionary<string, object>
                    {
                        { "friendId", friendId },
                        { "accepterUsername", accepterUsername }
                    },
                    Timestamp = DateTime.UtcNow.ToString("O")
                });

            // Also send to FriendHub for real-time friendship status updates
            await _friendHub.Clients.Group($"user_{userId}").SendAsync("FriendRequestAccepted", new
            {
                RequestId = "temp-id", // We don't have the requestId here, but it's not critical
                FriendId = friendId,
                FriendName = accepterUsername,
                FriendAvatar = (string?)null,
                Timestamp = DateTime.UtcNow.ToString("O")
            });
                
            _logger.LogInformation($"✅ Successfully created persistent notification and sent SignalR notifications to {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend request accepted notification");
        }
    }

    public async Task NotifyFriendRequestDeclined(string userId, string friendId, string declinerUsername)
    {
        try
        {
            _logger.LogInformation($"💔 FriendNotificationServiceSimple.NotifyFriendRequestDeclined called: userId={userId}, friendId={friendId}, declinerUsername={declinerUsername}");
            _logger.LogInformation($"📢 Creating persistent friend request declined notification from {declinerUsername} to {userId}");
            
            // First, create persistent notification in database
            await _notificationService.CreateNotificationAsync(
                targetUserId: userId,
                type: NotificationType.FriendRequestDeclined,
                title: "Friend Request Declined",
                message: $"{declinerUsername} declined your friend request",
                sourceUserId: friendId,
                data: new Dictionary<string, object>
                {
                    { "friendId", friendId },
                    { "declinerUsername", declinerUsername }
                }
            );
            
            // Then send real-time SignalR notification to the original requester  
            await _notificationHub.Clients.Group($"notifications_{userId}")
                .SendAsync("NewNotification", new { 
                    Type = "FriendRequestDeclined",
                    Title = "Friend Request Declined",
                    Message = $"{declinerUsername} declined your friend request",
                    SourceUserId = friendId,
                    TargetUserId = userId,
                    Data = new Dictionary<string, object>
                    {
                        { "friendId", friendId },
                        { "declinerUsername", declinerUsername }
                    },
                    Timestamp = DateTime.UtcNow.ToString("O")
                });
                
            _logger.LogInformation($"✅ Successfully created persistent notification and sent SignalR notification to {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend request declined notification");
        }
    }

    public async Task NotifyFriendAdded(string userId, string friendId, string friendUsername)
    {
        try
        {
            _logger.LogInformation($"📢 Creating persistent friend added notification for {friendUsername} to {userId}");
            
            // First, create persistent notification in database (using Other type since FriendAdded doesn't exist)
            await _notificationService.CreateNotificationAsync(
                targetUserId: userId,
                type: NotificationType.Other,
                title: "New Friend Added",
                message: $"You are now friends with {friendUsername}",
                sourceUserId: friendId,
                data: new Dictionary<string, object>
                {
                    { "friendId", friendId },
                    { "friendUsername", friendUsername },
                    { "type", "FriendAdded" } // Add custom type for frontend identification
                }
            );
            
            // Then send real-time SignalR notification to the user who accepted the friend request to update their friends list
            await _notificationHub.Clients.Group($"notifications_{userId}")
                .SendAsync("NewNotification", new { 
                    Type = "FriendAdded",
                    Title = "New Friend Added",
                    Message = $"You are now friends with {friendUsername}",
                    SourceUserId = friendId,
                    TargetUserId = userId,
                    Data = new Dictionary<string, object>
                    {
                        { "friendId", friendId },
                        { "friendUsername", friendUsername }
                    },
                    Timestamp = DateTime.UtcNow.ToString("O")
                });

            // Also send to FriendHub for real-time friendship status updates
            await _friendHub.Clients.Group($"user_{userId}").SendAsync("FriendAdded", new
            {
                FriendId = friendId,
                FriendName = friendUsername,
                FriendAvatar = (string?)null,
                Timestamp = DateTime.UtcNow.ToString("O")
            });
                
            _logger.LogInformation($"✅ Successfully created persistent notification and sent SignalR notifications to {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend added notification");
        }
    }

    public async Task NotifyFriendRemoved(string userId, string friendId, string removerUsername)
    {
        try
        {
            _logger.LogInformation($"📢 Creating persistent friend removed notification: {removerUsername} (userId: {userId}) removed friendId: {friendId}");
            
            // First, create persistent notification in database
            await _notificationService.CreateNotificationAsync(
                targetUserId: friendId,
                type: NotificationType.FriendRemoved,
                title: "Friend Removed",
                message: $"{removerUsername} removed you as a friend",
                sourceUserId: userId,
                data: new Dictionary<string, object>
                {
                    { "removerId", userId },
                    { "removerUsername", removerUsername }
                }
            );
            
            // Then send real-time SignalR notification to the friend who was removed via NotificationHub
            await _notificationHub.Clients.Group($"notifications_{friendId}")
                .SendAsync("NewNotification", new { 
                    Type = "FriendRemoved",
                    Title = "Friend Removed",
                    Message = $"{removerUsername} removed you as a friend",
                    SourceUserId = userId,
                    TargetUserId = friendId,
                    Data = new Dictionary<string, object>
                    {
                        { "removerId", userId },
                        { "removerUsername", removerUsername }
                    },
                    Timestamp = DateTime.UtcNow.ToString("O")
                });

            // Also send to FriendHub for real-time friendship status updates
            await _friendHub.Clients.Group($"user_{friendId}").SendAsync("FriendRemoved", new
            {
                RemovedFriendId = userId, // The person who was removed from friendId's perspective  
                RemoverName = removerUsername,
                Timestamp = DateTime.UtcNow.ToString("O")
            });

            _logger.LogInformation($"✅ Successfully created persistent notification and sent SignalR notifications to {friendId} about {removerUsername}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend removed notification");
        }
    }

    public async Task NotifyFriendRemovedByYou(string userId, string removedFriendId, string removedUsername)
    {
        try
        {
            _logger.LogInformation($"📢 Sending SignalR 'you removed friend' notification to userId: {userId} about {removedUsername}");
            
            // Send notification to the person who did the removing via NotificationHub
            await _notificationHub.Clients.Group($"notifications_{userId}")
                .SendAsync("NewNotification", new { 
                    Type = "FriendRemovedByYou",
                    Title = "Friend Removed",
                    Message = $"You removed {removedUsername} from your friends",
                    SourceUserId = userId,
                    TargetUserId = userId,
                    Data = new Dictionary<string, object>
                    {
                        { "removedUsername", removedUsername }
                    },
                    Timestamp = DateTime.UtcNow.ToString("O")
                });

            // Also send to FriendHub for real-time friendship status updates
            await _friendHub.Clients.Group($"user_{userId}").SendAsync("FriendRemoved", new
            {
                RemovedFriendId = removedFriendId, // Use the actual friend ID
                RemoverName = "You",
                Timestamp = DateTime.UtcNow.ToString("O")
            });

            _logger.LogInformation($"✅ Successfully sent 'you removed friend' SignalR notifications to {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send 'you removed friend' SignalR notification");
        }
    }
}
