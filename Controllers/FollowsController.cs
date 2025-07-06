using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Models;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FollowsController : ControllerBase
{
    private readonly MongoDbContext _context;

    public FollowsController(MongoDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Follow a user
    /// </summary>
    [HttpPost("{targetUserId}/follow")]
    public async Task<IActionResult> FollowUser(Guid targetUserId, [FromBody] Guid currentUserId)
    {
        // Check if already following
        var follower = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId.ToString())
            .FirstOrDefaultAsync();

        var followed = await _context.Users
            .Find(u => u.IdentityUserId == targetUserId.ToString())
            .FirstOrDefaultAsync();

        if (follower == null || followed == null)
        {
            return NotFound("One or both users not found");
        }

        if (follower.FollowedUsers.Any(fu => fu.Id == followed.Id))
        {
            return BadRequest("Already following this user");
        }

        // Prevent self-following
        if (currentUserId == targetUserId)
        {
            return BadRequest("Cannot follow yourself");
        }

        await _context.Users.UpdateOneAsync(
            u => u.Id == follower.Id,
            Builders<Models.User>.Update
                .Push(u => u.FollowedUsers, new UserReference { Id = followed.Id })
                .Set(u => u.UpdatedAt, DateTime.UtcNow));

        await _context.Users.UpdateOneAsync(
            u => u.Id == followed.Id,
            Builders<Models.User>.Update
                .Push(u => u.Followers, new UserReference { Id = follower.Id })
                .Set(u => u.UpdatedAt, DateTime.UtcNow));

        return Ok(new { message = "Successfully followed user" });
    }

    /// <summary>
    /// Unfollow a user
    /// </summary>
    [HttpDelete("{targetUserId}/follow")]
    public async Task<IActionResult> UnfollowUser(Guid targetUserId, [FromBody] Guid currentUserId)
    {
        var follower = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId.ToString())
            .FirstOrDefaultAsync();

        var followed = await _context.Users
            .Find(u => u.IdentityUserId == targetUserId.ToString())
            .FirstOrDefaultAsync();

        if (follower == null || followed == null)
        {
            return NotFound("One or both users not found");
        }

        if (!follower.FollowedUsers.Any(fu => fu.Id == followed.Id))
        {
            return NotFound("Not following this user");
        }

        await _context.Users.UpdateOneAsync(
            u => u.Id == follower.Id,
            Builders<Models.User>.Update
                .PullFilter(u => u.FollowedUsers, fu => fu.Id == followed.Id)
                .Set(u => u.UpdatedAt, DateTime.UtcNow));

        await _context.Users.UpdateOneAsync(
            u => u.Id == followed.Id,
            Builders<Models.User>.Update
                .PullFilter(u => u.Followers, f => f.Id == follower.Id)
                .Set(u => u.UpdatedAt, DateTime.UtcNow));

        return Ok(new { message = "Successfully unfollowed user" });
    }

    /// <summary>
    /// Get followers of a user
    /// </summary>
    [HttpGet("{userId}/followers")]
    public async Task<ActionResult<IEnumerable<Guid>>> GetFollowers(Guid userId)
    {
        var user = await _context.Users
            .Find(u => u.IdentityUserId == userId.ToString())
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return NotFound();
        }

        var followerIds = new List<Guid>();
        foreach (var follower in user.Followers)
        {
            var followerUser = await _context.Users
                .Find(u => u.Id == follower.Id)
                .FirstOrDefaultAsync();
            
            if (followerUser != null && Guid.TryParse(followerUser.IdentityUserId, out var identityId))
            {
                followerIds.Add(identityId);
            }
        }

        return Ok(followerIds);
    }

    /// <summary>
    /// Get users that a user is following
    /// </summary>
    [HttpGet("{userId}/following")]
    public async Task<ActionResult<IEnumerable<Guid>>> GetFollowing(Guid userId)
    {
        var user = await _context.Users
            .Find(u => u.IdentityUserId == userId.ToString())
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return NotFound();
        }

        var followingIds = new List<Guid>();
        foreach (var followed in user.FollowedUsers)
        {
            var followedUser = await _context.Users
                .Find(u => u.Id == followed.Id)
                .FirstOrDefaultAsync();
            
            if (followedUser != null && Guid.TryParse(followedUser.IdentityUserId, out var identityId))
            {
                followingIds.Add(identityId);
            }
        }

        return Ok(followingIds);
    }

    /// <summary>
    /// Check if user A follows user B
    /// </summary>
    [HttpGet("{followerId}/follows/{followedId}")]
    public async Task<ActionResult<bool>> CheckIfFollowing(Guid followerId, Guid followedId)
    {
        var follower = await _context.Users
            .Find(u => u.IdentityUserId == followerId.ToString())
            .FirstOrDefaultAsync();

        var followed = await _context.Users
            .Find(u => u.IdentityUserId == followedId.ToString())
            .FirstOrDefaultAsync();

        if (follower == null || followed == null)
        {
            return NotFound();
        }

        var isFollowing = follower.FollowedUsers.Any(fu => fu.Id == followed.Id);

        return Ok(isFollowing);
    }

    /// <summary>
    /// Get follow statistics for a user
    /// </summary>
    [HttpGet("{userId}/stats")]
    public async Task<ActionResult> GetFollowStats(Guid userId)
    {
        var user = await _context.Users
            .Find(u => u.IdentityUserId == userId.ToString())
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return NotFound();
        }

        return Ok(new 
        { 
            userId,
            followerCount = user.Followers.Count,
            followingCount = user.FollowedUsers.Count
        });
    }
} 