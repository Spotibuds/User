using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Hubs;

namespace User.Services;

public class FriendNotificationService : IFriendNotificationService
{
    private readonly IHubContext<FriendHub> _friendHub;
    private readonly ILogger<FriendNotificationService> _logger;
    private readonly MongoDbContext _context;

    public FriendNotificationService(IHubContext<FriendHub> friendHub, ILogger<FriendNotificationService> logger, MongoDbContext context)
    {
        _friendHub = friendHub;
        _logger = logger;
        _context = context;
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

            // Notify target user about receiving a friend request
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

            // Notify the original requester about acceptance
            await _friendHub.Clients.Group($"user_{friendId}").SendAsync("FriendRequestAccepted", new
            {
                requestId = friendship?.Id ?? "unknown", // Match frontend expectation (lowercase)
                friendId = userId, // The person who accepted (becomes their new friend)
                friendName = accepter.UserName ?? "Unknown User",
                friendAvatar = accepter.AvatarUrl,
                Timestamp = DateTime.UtcNow.ToString("O") // ISO 8601 format
            });

            _logger.LogInformation($"Friend request accepted notification sent to {friendId} by {accepterUsername}");
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
            
            if (decliner == null)
            {
                _logger.LogError($"Could not find decliner user: {userId}");
                return;
            }

            // Notify the original requester about decline
            await _friendHub.Clients.Group($"user_{friendId}").SendAsync("FriendRequestDeclined", new
            {
                RequestId = "declined", // We don't have the request ID here since it might be deleted
                DeclinedBy = userId,
                Timestamp = DateTime.UtcNow.ToString("O") // ISO 8601 format
            });

            _logger.LogInformation($"Friend request declined notification sent to {friendId} by {declinerUsername}");
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
            // userId = the person being notified (the one being removed from the other's friends)
            // friendId = the person who initiated the removal
            
            // Get user details from database
            var removedUser = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            var removerUser = await _context.Users.Find(u => u.IdentityUserId == friendId).FirstOrDefaultAsync();
            
            if (removedUser == null || removerUser == null)
            {
                _logger.LogError($"Could not find users for removal notification: removed={userId}, remover={friendId}");
                return;
            }

            // Notify the person who got removed from friends list
            await _friendHub.Clients.Group($"user_{userId}").SendAsync("FriendRemoved", new
            {
                RemovedFriendId = friendId, // The person who removed them
                RemovedByName = removerUser.UserName ?? "Unknown User",
                Timestamp = DateTime.UtcNow.ToString("O") // ISO 8601 format
            });

            // Also notify the person who initiated the removal (for their own UI update)
            await _friendHub.Clients.Group($"user_{friendId}").SendAsync("FriendRemoved", new
            {
                RemovedFriendId = userId, // The person who was removed from their friends
                RemovedByName = "You", 
                Timestamp = DateTime.UtcNow.ToString("O") // ISO 8601 format
            });

            _logger.LogInformation($"Friend removed notification sent: {removerUsername} (ID: {friendId}) removed {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send friend removed notification");
        }
    }
} 