using Microsoft.AspNetCore.SignalR;
using User.Hubs;

namespace User.Services;

public class FriendNotificationServiceSimple : IFriendNotificationService
{
    private readonly IHubContext<NotificationHub> _notificationHub;
    private readonly IHubContext<FriendHub> _friendHub;
    private readonly ILogger<FriendNotificationServiceSimple> _logger;

    public FriendNotificationServiceSimple(
        IHubContext<NotificationHub> notificationHub,
        IHubContext<FriendHub> friendHub, 
        ILogger<FriendNotificationServiceSimple> logger)
    {
        _notificationHub = notificationHub;
        _friendHub = friendHub;
        _logger = logger;
    }

    public async Task NotifyFriendRequestSent(string userId, string targetUserId, string requesterUsername, string friendshipId)
    {
        try
        {
            _logger.LogInformation($"📢 Sending SignalR friend request notification from {requesterUsername} to {targetUserId} (FriendshipId: {friendshipId})");
            
            // Send notification to the target user's notification group using the same event name as persistent notifications
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
                
            _logger.LogInformation($"✅ Successfully sent friend request SignalR notification to {targetUserId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend request SignalR notification");
        }
    }

    public async Task NotifyFriendRequestAccepted(string userId, string friendId, string accepterUsername)
    {
        try
        {
            _logger.LogInformation($"📢 Sending SignalR friend request accepted notification from {accepterUsername} to {userId}");
            
            // Send notification to the original requester via NotificationHub
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
                
            _logger.LogInformation($"✅ Successfully sent friend request accepted SignalR notifications to {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend request accepted SignalR notification");
        }
    }

    public async Task NotifyFriendRequestDeclined(string userId, string friendId, string declinerUsername)
    {
        try
        {
            _logger.LogInformation($"� FriendNotificationServiceSimple.NotifyFriendRequestDeclined called: userId={userId}, friendId={friendId}, declinerUsername={declinerUsername}");
            _logger.LogInformation($"�📢 Sending SignalR friend request declined notification from {declinerUsername} to {userId}");
            
            // Send notification to the original requester  
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
                
            _logger.LogInformation($"✅ Successfully sent friend request declined SignalR notification to {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend request declined SignalR notification");
        }
    }

    public async Task NotifyFriendAdded(string userId, string friendId, string friendUsername)
    {
        try
        {
            _logger.LogInformation($"📢 Sending SignalR friend added notification for {friendUsername} to {userId}");
            
            // Send notification to the user who accepted the friend request to update their friends list
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
                
            _logger.LogInformation($"✅ Successfully sent friend added SignalR notifications to {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend added SignalR notification");
        }
    }

    public async Task NotifyFriendRemoved(string userId, string friendId, string removerUsername)
    {
        try
        {
            _logger.LogInformation($"📢 Sending SignalR friend removed notification: {removerUsername} (userId: {userId}) removed friendId: {friendId}");
            
            // Send notification to the friend who was removed via NotificationHub
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

            _logger.LogInformation($"✅ Successfully sent friend removed SignalR notifications to {friendId} about {removerUsername}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send friend removed SignalR notification");
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
