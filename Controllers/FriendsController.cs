using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Models;
using User.Services;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FriendsController : ControllerBase
{
    private readonly MongoDbContext _context;
    private readonly IFriendNotificationService _notificationService;
    private readonly INotificationService _persistentNotificationService;
    private readonly ILogger<FriendsController> _logger;

    public FriendsController(MongoDbContext context, IFriendNotificationService notificationService, INotificationService persistentNotificationService, ILogger<FriendsController> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _persistentNotificationService = persistentNotificationService;
        _logger = logger;
    }

    /// <summary>
    /// Reset all friendships (for development/testing)
    /// </summary>
    [HttpDelete("reset")]
    public async Task<IActionResult> ResetAllFriendships()
    {
        try
        {
            if (!_context.IsConnected || _context.Friends == null)
            {
                return StatusCode(503, new { message = "Database temporarily unavailable" });
            }

            await _context.Friends.DeleteManyAsync(Builders<Entities.Friend>.Filter.Empty);
            return Ok(new { message = "All friendships have been reset" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Failed to reset friendships", error = ex.Message });
        }
    }

    /// <summary>
    /// Send a friend request
    /// </summary>
    [HttpPost("request")]
    public async Task<IActionResult> SendFriendRequest([FromBody] SendFriendRequestDto dto)
    {
        if (!_context.IsConnected || _context.Users == null || _context.Friends == null)
        {
            return StatusCode(503, new { message = "Database temporarily unavailable" });
        }

        // Get requester ID from JWT token
        var requesterId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(requesterId))
        {
            return Unauthorized("User ID not found in token");
        }

        // Validate that both users exist
        var requester = await _context.Users
            .Find(u => u.IdentityUserId == requesterId)
            .FirstOrDefaultAsync();

        var target = await _context.Users
            .Find(u => u.IdentityUserId == dto.ToUserId)
            .FirstOrDefaultAsync();

        if (requester == null || target == null)
        {
            return NotFound("One or both users not found");
        }

        // Check if friendship already exists
        var existingFriendship = await _context.Friends
            .Find(f => (f.UserId == requester.Id && f.FriendId == target.Id) ||
                      (f.UserId == target.Id && f.FriendId == requester.Id))
            .FirstOrDefaultAsync();

        if (existingFriendship != null)
        {
            if (existingFriendship.Status == Entities.FriendStatus.Pending)
            {
                return BadRequest("Friend request already pending");
            }
            else if (existingFriendship.Status == Entities.FriendStatus.Accepted)
            {
                return BadRequest("Users are already friends");
            }
            else if (existingFriendship.Status == Entities.FriendStatus.Declined)
            {
                // Delete the declined friendship to allow a new request
                await _context.Friends.DeleteOneAsync(f => f.Id == existingFriendship.Id);
            }
        }

        var friendship = new Entities.Friend
        {
            UserId = requester.Id,
            FriendId = target.Id,
            Status = Entities.FriendStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        await _context.Friends.InsertOneAsync(friendship);

        // Note: Using only real-time notifications for friend requests to prevent duplicates
        // The frontend will receive these instantly via SignalR

        // Send real-time notification via SignalR for instant delivery
        await _notificationService.NotifyFriendRequestSent(
            requester.IdentityUserId, 
            target.IdentityUserId, 
            requester.UserName ?? "Unknown User",
            friendship.Id  // Pass the friendship ID
        );

        return Ok(new { 
            message = "Friend request sent successfully", 
            friendshipId = friendship.Id,
            status = "pending"
        });
    }

    /// <summary>
    /// Accept a friend request
    /// </summary>
    [HttpPost("{friendshipId}/accept")]
    public async Task<IActionResult> AcceptFriendRequest(string friendshipId, [FromBody] AcceptFriendRequestDto dto)
    {
        if (string.IsNullOrEmpty(friendshipId))
        {
            return BadRequest("Friendship ID is required");
        }

        if (!_context.IsConnected || _context.Friends == null || _context.Users == null)
        {
            return StatusCode(503, new { message = "Database temporarily unavailable" });
        }

        var friendship = await _context.Friends
            .Find(f => f.Id == friendshipId)
            .FirstOrDefaultAsync();

        if (friendship == null)
        {
            return NotFound("Friend request not found");
        }

        // Validate that the user exists
        var user = await _context.Users
            .Find(u => u.IdentityUserId == dto.UserId)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return NotFound("User not found");
        }

        // Validate that the current user is the friend (target of the request)
        if (friendship.FriendId != user.Id)
        {
            return BadRequest("Unauthorized to accept this friend request");
        }

        if (friendship.Status != Entities.FriendStatus.Pending)
        {
            return BadRequest("Friend request is not pending");
        }

        // Update friendship status
        await _context.Friends.UpdateOneAsync(
            f => f.Id == friendshipId,
            Builders<Entities.Friend>.Update
                .Set(f => f.Status, Entities.FriendStatus.Accepted)
                .Set(f => f.AcceptedAt, DateTime.UtcNow));

        // Send real-time notification to the requester (person who sent the request)
        var requester = await _context.Users.Find(u => u.Id == friendship.UserId).FirstOrDefaultAsync();
        await _notificationService.NotifyFriendRequestAccepted(
            requester?.IdentityUserId ?? friendship.UserId,
            user.IdentityUserId,
            user.UserName
        );

        // Send real-time notification to the accepter (person who accepted the request) so their friends list updates
        if (requester != null)
        {
            await _notificationService.NotifyFriendAdded(
                user.IdentityUserId,
                requester.IdentityUserId,
                requester.UserName
            );
        }

        return Ok(new
        {
            message = "Friend request accepted",
            friendshipId = friendship.Id,
            status = "accepted"
        });
    }

    /// <summary>
    /// Decline a friend request
    /// </summary>
    [HttpPost("{friendshipId}/decline")]
    public async Task<IActionResult> DeclineFriendRequest(string friendshipId, [FromBody] DeclineFriendRequestDto dto)
    {
        if (string.IsNullOrEmpty(friendshipId))
        {
            return BadRequest("Friendship ID is required");
        }

        if (!_context.IsConnected || _context.Friends == null || _context.Users == null)
        {
            return StatusCode(503, new { message = "Database temporarily unavailable" });
        }

        var friendship = await _context.Friends
            .Find(f => f.Id == friendshipId)
            .FirstOrDefaultAsync();

        if (friendship == null)
        {
            return NotFound("Friend request not found");
        }

        // Validate that the user exists
        var user = await _context.Users
            .Find(u => u.IdentityUserId == dto.UserId)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return NotFound("User not found");
        }

        // Validate that the current user is the friend (target of the request)
        if (friendship.FriendId != user.Id)
        {
            return BadRequest("Unauthorized to decline this friend request");
        }

        if (friendship.Status != Entities.FriendStatus.Pending)
        {
            return BadRequest("Friend request is not pending");
        }

        // Update friendship status to declined
        await _context.Friends.UpdateOneAsync(
            f => f.Id == friendshipId,
            Builders<Entities.Friend>.Update
                .Set(f => f.Status, Entities.FriendStatus.Declined));

        // Send real-time notification only (no persistent notification to avoid duplicates)
        var requester = await _context.Users.Find(u => u.Id == friendship.UserId).FirstOrDefaultAsync();
        
        _logger.LogInformation($"🔍 DEBUGGING: About to send decline notification to requester {requester?.IdentityUserId} from {user.UserName}");
        
        await _notificationService.NotifyFriendRequestDeclined(
            requester?.IdentityUserId ?? friendship.UserId,
            user.IdentityUserId,
            user.UserName
        );
        
        _logger.LogInformation($"✅ DEBUGGING: Decline notification sent successfully");

        return Ok(new
        {
            message = "Friend request declined",
            friendshipId = friendship.Id,
            status = "declined"
        });
    }

    /// <summary>
    /// Get pending friend requests for a user
    /// </summary>
    [HttpGet("pending/{userId}")]
    public async Task<ActionResult<IEnumerable<object>>> GetPendingFriendRequests(string userId)
    {
        if (!_context.IsConnected || _context.Friends == null || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            // First find the user by IdentityUserId
            var user = await _context.Users
                .Find(u => u.IdentityUserId == userId)
                .FirstOrDefaultAsync();
                
            if (user == null)
            {
                return NotFound("User not found");
            }
                
            var pendingRequests = await _context.Friends
                .Find(f => f.FriendId == user.Id && f.Status == Entities.FriendStatus.Pending)
                .ToListAsync();

            var result = new List<object>();

            foreach (var request in pendingRequests)
            {
                var requester = await _context.Users
                    .Find(u => u.Id == request.UserId)
                    .FirstOrDefaultAsync();

                result.Add(new
                {
                    RequestId = request.Id,
                    RequesterId = requester?.IdentityUserId ?? request.UserId,
                    RequesterUsername = requester?.UserName ?? "Unknown User",
                    RequesterAvatar = requester?.AvatarUrl,
                    RequestedAt = request.CreatedAt
                });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get friends list for a user
    /// </summary>
    [HttpGet("{userId}")]
    public async Task<ActionResult<IEnumerable<string>>> GetFriends(string userId)
    {
        try
        {
            if (!_context.IsConnected || _context.Friends == null)
            {
                return StatusCode(503, new { message = "Database temporarily unavailable" });
            }

            // First find the user by IdentityUserId
            var user = await _context.Users
                .Find(u => u.IdentityUserId == userId)
                .FirstOrDefaultAsync();
                
            if (user == null)
            {
                return NotFound("User not found");
            }
                
            var friendships = await _context.Friends
                .Find(f => (f.UserId == user.Id || f.FriendId == user.Id) && f.Status == Entities.FriendStatus.Accepted)
                .ToListAsync();

            var friendIds = new List<string>();
            foreach (var friendship in friendships)
            {
                var friendMongoId = friendship.UserId == user.Id ? friendship.FriendId : friendship.UserId;
                var friend = await _context.Users.Find(u => u.Id == friendMongoId).FirstOrDefaultAsync();
                if (friend != null && !string.IsNullOrWhiteSpace(friend.IdentityUserId))
                {
                    friendIds.Add(friend.IdentityUserId);
                }
                // Skip orphaned friendships instead of returning a Mongo _id that the client cannot resolve
            }

            return Ok(friendIds);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Get friendship status between two users
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<object>> GetFriendshipStatus([FromQuery] string userId1, [FromQuery] string userId2)
    {
        if (!_context.IsConnected || _context.Friends == null)
        {
            return StatusCode(503, new { message = "Database temporarily unavailable" });
        }

        // First find both users by IdentityUserId
        var user1 = await _context.Users
            .Find(u => u.IdentityUserId == userId1)
            .FirstOrDefaultAsync();
        
        var user2 = await _context.Users
            .Find(u => u.IdentityUserId == userId2)
            .FirstOrDefaultAsync();
        
        if (user1 == null || user2 == null)
        {
            return Ok(new { status = "None" });
        }
        
        var friendship = await _context.Friends
            .Find(f => (f.UserId == user1.Id && f.FriendId == user2.Id) ||
                      (f.UserId == user2.Id && f.FriendId == user1.Id))
            .FirstOrDefaultAsync();

        if (friendship == null)
        {
            return Ok(new { status = "None" });
        }

        // Get the IdentityUserId for both users to return in the response
        var requester = await _context.Users.Find(u => u.Id == friendship.UserId).FirstOrDefaultAsync();
        var addressee = await _context.Users.Find(u => u.Id == friendship.FriendId).FirstOrDefaultAsync();

        return Ok(new
        {
            status = friendship.Status.ToString().ToLower(),
            friendshipId = friendship.Id,
            requesterId = requester?.IdentityUserId ?? friendship.UserId, // The IdentityUserId of who sent the request
            addresseeId = addressee?.IdentityUserId ?? friendship.FriendId, // The IdentityUserId of who received the request
            createdAt = friendship.CreatedAt,
            acceptedAt = friendship.AcceptedAt
        });
    }

    /// <summary>
    /// Remove a friend
    /// </summary>
    [HttpDelete("{friendshipId}")]
    public async Task<IActionResult> RemoveFriend(string friendshipId, [FromBody] RemoveFriendDto dto)
    {
        if (!_context.IsConnected || _context.Friends == null || _context.Users == null)
        {
            return StatusCode(503, new { message = "Database temporarily unavailable" });
        }

        var friendship = await _context.Friends
            .Find(f => f.Id == friendshipId)
            .FirstOrDefaultAsync();

        if (friendship == null)
        {
            return NotFound("Friendship not found");
        }

        // Validate that the user exists
        var user = await _context.Users
            .Find(u => u.IdentityUserId == dto.UserId)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return NotFound("User not found");
        }

        // Validate that the current user is involved in the friendship
        if (friendship.UserId != user.Id && friendship.FriendId != user.Id)
        {
            return BadRequest("Unauthorized to remove this friendship");
        }

        if (friendship.Status != Entities.FriendStatus.Accepted)
        {
            return BadRequest("Can only remove accepted friendships");
        }

        // Delete the friendship
        await _context.Friends.DeleteOneAsync(f => f.Id == friendshipId);

        // Determine the other user ID (the one being removed from)
        string otherUserId = friendship.UserId == user.Id ? friendship.FriendId : friendship.UserId;
        var otherUser = await _context.Users.Find(u => u.Id == otherUserId).FirstOrDefaultAsync();
        
        if (otherUser != null)
        {
            _logger.LogDebug($"Sending friend removed notification: {user.UserName} (remover) -> {otherUser.UserName} (recipient)");
            
            // Send real-time notification to the other user (the one who was removed)
            // Parameters: userId (remover), friendId (recipient), removerUsername
            await _notificationService.NotifyFriendRemoved(
                user.IdentityUserId,        // userId = the remover (source of the notification)
                otherUser.IdentityUserId,   // friendId = the recipient (person who was removed)
                user.UserName               // removerUsername = name of the person who did the removing
            );

            // Also notify the remover that they successfully removed the friend
            await _notificationService.NotifyFriendRemovedByYou(
                user.IdentityUserId,        // The person who did the removing
                otherUser.IdentityUserId,   // ID of the person who was removed
                otherUser.UserName          // Name of the person who was removed
            );
        }
        else
        {
            _logger.LogWarning($"Could not find other user with ID {otherUserId} for friend removal notification");
        }

        return Ok(new { message = "Friend removed successfully" });
    }
}

// DTOs
public class SendFriendRequestDto
{
    public string ToUserId { get; set; } = string.Empty;
}

public class AcceptFriendRequestDto
{
    public string UserId { get; set; } = string.Empty;
}

public class DeclineFriendRequestDto
{
    public string UserId { get; set; } = string.Empty;
}

public class RemoveFriendDto
{
    public string UserId { get; set; } = string.Empty;
} 