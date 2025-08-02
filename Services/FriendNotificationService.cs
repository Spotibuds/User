using Microsoft.AspNetCore.SignalR;
using User.Hubs;

namespace User.Services;

public class FriendNotificationService : IFriendNotificationService
{
    private readonly IHubContext<FriendHub> _friendHub;
    private readonly ILogger<FriendNotificationService> _logger;

    public FriendNotificationService(IHubContext<FriendHub> friendHub, ILogger<FriendNotificationService> logger)
    {
        _friendHub = friendHub;
        _logger = logger;
    }

    public async Task NotifyFriendRequestSent(string userId, string targetUserId, string requesterUsername)
    {
        try
        {
            // Notify all connected clients about the new friend request
            await _friendHub.Clients.All.SendAsync("FriendRequestReceived", new
            {
                userId,
                targetUserId,
                requesterUsername,
                requestedAt = DateTime.UtcNow
            });

            _logger.LogInformation($"Friend request notification sent from {requesterUsername} to user {targetUserId}");
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
            // Notify all connected clients about the accepted friend request
            await _friendHub.Clients.All.SendAsync("FriendRequestAccepted", new
            {
                userId,
                friendId,
                accepterUsername,
                acceptedAt = DateTime.UtcNow
            });

            _logger.LogInformation($"Friend request accepted notification sent to {userId} by {accepterUsername}");
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
            // Notify all connected clients about the declined friend request
            await _friendHub.Clients.All.SendAsync("FriendRequestDeclined", new
            {
                userId,
                friendId,
                declinerUsername,
                declinedAt = DateTime.UtcNow
            });

            _logger.LogInformation($"Friend request declined notification sent to {userId} by {declinerUsername}");
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
            // Notify all connected clients about the friend removal
            await _friendHub.Clients.All.SendAsync("FriendRemoved", new
            {
                userId,
                friendId,
                removerUsername,
                removedAt = DateTime.UtcNow
            });

            _logger.LogInformation($"Friend removed notification sent by {removerUsername}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send friend removed notification");
        }
    }
} 