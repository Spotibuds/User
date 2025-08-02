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
    [HttpPost]
    public async Task<IActionResult> FollowUser([FromBody] FollowRequestDto dto)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            Console.WriteLine("FollowUser: MongoDB not connected or Users collection is null");
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            // Prevent self-following
            if (dto.FollowerId == dto.FollowedId)
            {
                return BadRequest("Cannot follow yourself");
            }

            // Check if users exist
            var follower = await _context.Users
                .Find(u => u.IdentityUserId == dto.FollowerId)
                .FirstOrDefaultAsync();

            var followed = await _context.Users
                .Find(u => u.IdentityUserId == dto.FollowedId)
                .FirstOrDefaultAsync();

            if (follower == null || followed == null)
            {
                return NotFound("One or both users not found");
            }

            // Check if already following
            if (follower.FollowedUsers.Any(fu => fu.Id == followed.Id))
            {
                return BadRequest("Already following this user");
            }

            // Add to follower's following list
            await _context.Users.UpdateOneAsync(
                u => u.Id == follower.Id,
                Builders<Models.User>.Update
                    .Push(u => u.FollowedUsers, new UserReference { Id = followed.Id })
                    .Set(u => u.UpdatedAt, DateTime.UtcNow));

            // Add to followed user's followers list
            await _context.Users.UpdateOneAsync(
                u => u.Id == followed.Id,
                Builders<Models.User>.Update
                    .Push(u => u.Followers, new UserReference { Id = follower.Id })
                    .Set(u => u.UpdatedAt, DateTime.UtcNow));

            return Ok(new { message = "Successfully followed user" });
        }
        catch (MongoDB.Driver.MongoConnectionException ex)
        {
            return StatusCode(503, new { message = "Database temporarily unavailable", error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Unfollow a user
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> UnfollowUser([FromBody] FollowRequestDto dto)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            Console.WriteLine("UnfollowUser: MongoDB not connected or Users collection is null");
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        var follower = await _context.Users
            .Find(u => u.IdentityUserId == dto.FollowerId)
            .FirstOrDefaultAsync();

        var followed = await _context.Users
            .Find(u => u.IdentityUserId == dto.FollowedId)
            .FirstOrDefaultAsync();

        if (follower == null || followed == null)
        {
            return NotFound("One or both users not found");
        }

        if (!follower.FollowedUsers.Any(fu => fu.Id == followed.Id))
        {
            return NotFound("Not following this user");
        }

        // Remove from follower's following list
        await _context.Users.UpdateOneAsync(
            u => u.Id == follower.Id,
            Builders<Models.User>.Update
                .PullFilter(u => u.FollowedUsers, fu => fu.Id == followed.Id)
                .Set(u => u.UpdatedAt, DateTime.UtcNow));

        // Remove from followed user's followers list
        await _context.Users.UpdateOneAsync(
            u => u.Id == followed.Id,
            Builders<Models.User>.Update
                .PullFilter(u => u.Followers, f => f.Id == follower.Id)
                .Set(u => u.UpdatedAt, DateTime.UtcNow));

        return Ok(new { message = "Successfully unfollowed user" });
    }

    /// <summary>
    /// Get followers for a user
    /// </summary>
    [HttpGet("{userId}/followers")]
    public async Task<ActionResult<IEnumerable<string>>> GetFollowers(string userId)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            Console.WriteLine("GetFollowers: MongoDB not connected or Users collection is null");
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var user = await _context.Users
                .Find(u => u.IdentityUserId == userId)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                return NotFound("User not found");
            }

            var followerIds = user.Followers.Select(f => f.Id).ToList();

            return Ok(followerIds);
        }
        catch (MongoDB.Driver.MongoConnectionException ex)
        {
            return StatusCode(503, new { message = "Database temporarily unavailable", error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Get users that a user is following
    /// </summary>
    [HttpGet("{userId}/following")]
    public async Task<ActionResult<IEnumerable<string>>> GetFollowing(string userId)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            Console.WriteLine("GetFollowing: MongoDB not connected or Users collection is null");
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var user = await _context.Users
                .Find(u => u.IdentityUserId == userId)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                return NotFound("User not found");
            }

            var followingIds = user.FollowedUsers.Select(f => f.Id).ToList();

            return Ok(followingIds);
        }
        catch (MongoDB.Driver.MongoConnectionException ex)
        {
            return StatusCode(503, new { message = "Database temporarily unavailable", error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Check if user A follows user B
    /// </summary>
    [HttpGet("check")]
    public async Task<ActionResult<bool>> CheckIfFollowing([FromQuery] string followerId, [FromQuery] string followedId)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            Console.WriteLine("CheckIfFollowing: MongoDB not connected or Users collection is null");
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        var follower = await _context.Users
            .Find(u => u.IdentityUserId == followerId)
            .FirstOrDefaultAsync();

        var followed = await _context.Users
            .Find(u => u.IdentityUserId == followedId)
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
    public async Task<ActionResult> GetFollowStats(string userId)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            Console.WriteLine("GetFollowStats: MongoDB not connected or Users collection is null");
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        var user = await _context.Users
            .Find(u => u.IdentityUserId == userId)
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

public class FollowRequestDto
{
    public string FollowerId { get; set; } = string.Empty;
    public string FollowedId { get; set; } = string.Empty;
} 