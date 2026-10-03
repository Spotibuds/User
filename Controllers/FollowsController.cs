using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Services;
namespace User.Controllers;
[ApiController, Route("api/follows")]
public class FollowsController(MongoDbContext db, ProfilePolicy profiles, SocialCommands commands) : ControllerBase
{
    [HttpPost] public async Task<object> Follow(FollowRequestDto dto) { await commands.Follow(Input.Actor(User), dto.FollowedId, true); return new { message = "Following saved" }; }
    [HttpDelete] public async Task<object> Unfollow(FollowRequestDto dto) { await commands.Follow(Input.Actor(User), dto.FollowedId, false); return new { message = "Following removed" }; }
    [HttpGet("{userId}/followers")] public async Task<object> Followers(string userId, int limit = 100, int skip = 0) { await profiles.Read(User, userId); Input.Page(limit, skip); var ids = await db.Follows.Find(x => x.FollowedId == userId).Skip(skip).Limit(limit).Project(x => x.FollowerId).ToListAsync(); return await db.Users.Find(x => ids.Contains(x.IdentityUserId)).Project(x => x.Id).ToListAsync(); }
    [HttpGet("{userId}/following")] public async Task<object> Following(string userId, int limit = 100, int skip = 0) { await profiles.Read(User, userId); Input.Page(limit, skip); var ids = await db.Follows.Find(x => x.FollowerId == userId).Skip(skip).Limit(limit).Project(x => x.FollowedId).ToListAsync(); return await db.Users.Find(x => ids.Contains(x.IdentityUserId)).Project(x => x.Id).ToListAsync(); }
    [HttpGet("check")] public async Task<object> Check(string followerId, string followedId) { Input.Owner(User, followerId); Input.GuidId(followedId); return await db.Follows.Find(x => x.FollowerId == followerId && x.FollowedId == followedId).AnyAsync(); }
    [HttpGet("{userId}/stats")] public async Task<object> Stats(string userId) { await profiles.Read(User, userId); return new { userId, followerCount = await db.Follows.CountDocumentsAsync(x => x.FollowedId == userId), followingCount = await db.Follows.CountDocumentsAsync(x => x.FollowerId == userId) }; }
}
public class FollowRequestDto { public string? FollowerId { get; set; } public string FollowedId { get; set; } = ""; }
