using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Services;
namespace User.Controllers;
[ApiController, Route("api/friends")]
public class FriendsController(MongoDbContext db, ProfilePolicy profiles, SocialCommands commands) : ControllerBase
{
    [HttpPost("request")] public async Task<object> SendRequest(SendFriendRequestDto dto) { var friend = await commands.Request(Input.Actor(User), dto.TargetUserId); return new { message = "Friend request saved", friendshipId = friend.Id }; }
    [HttpPost("{id}/accept")] public async Task<object> Accept(string id) { await commands.Transition(Input.Actor(User), id, FriendStatus.Accepted); return new { message = "Friend request accepted" }; }
    [HttpPost("{id}/decline")] public async Task<object> Decline(string id) { await commands.Transition(Input.Actor(User), id, FriendStatus.Declined); return new { message = "Friend request declined" }; }
    [HttpDelete("{id}")] public async Task<object> Remove(string id) { await commands.Remove(Input.Actor(User), id); return new { message = "Friend removed" }; }
    [HttpGet("pending/{userId}")]
    public async Task<object> Pending(string userId)
    {
        Input.Owner(User, userId); var user = await profiles.Find(userId); var friends = await db.Friends.Find(x => x.FriendId == user.Id && x.Status == FriendStatus.Pending).SortByDescending(x => x.CreatedAt).Limit(100).ToListAsync();
        var ids = friends.Select(x => x.UserId).ToList(); var senders = await db.Users.Find(x => ids.Contains(x.Id)).Limit(100).ToListAsync();
        return friends.Select(x => new { requestId = x.Id, requesterId = senders.FirstOrDefault(s => s.Id == x.UserId)?.IdentityUserId, requesterUsername = senders.FirstOrDefault(s => s.Id == x.UserId)?.UserName, requesterAvatar = senders.FirstOrDefault(s => s.Id == x.UserId)?.AvatarUrl, requestedAt = x.CreatedAt });
    }
    [HttpGet("{userId}")]
    public async Task<object> Friends(string userId)
    {
        var user = await profiles.Read(User, userId); var friends = await db.Friends.Find(x => (x.FriendId == user.Id || x.UserId == user.Id) && x.Status == FriendStatus.Accepted).Limit(100).ToListAsync();
        var ids = friends.Select(x => x.FriendId == user.Id ? x.UserId : x.FriendId).ToList(); return await db.Users.Find(x => ids.Contains(x.Id)).Project(x => x.IdentityUserId).ToListAsync();
    }
    [HttpGet("status")]
    public async Task<object> Status(string userId1, string userId2)
    {
        Input.Owner(User, userId1); var a = await profiles.Find(userId1); var b = await profiles.Find(userId2);
        var friend = await db.Friends.Find(x => x.PairKey == SocialCommands.Pair(a.Id, b.Id)).FirstOrDefaultAsync();
        if (friend == null) return new { status = "None" };
        return new { status = friend.Status.ToString().ToLowerInvariant(), friendshipId = friend.Id, requesterId = friend.UserId == a.Id ? a.IdentityUserId : b.IdentityUserId, addresseeId = friend.FriendId == a.Id ? a.IdentityUserId : b.IdentityUserId, friend.CreatedAt, friend.AcceptedAt };
    }
}
public class SendFriendRequestDto { public string TargetUserId { get; set; } = ""; }
