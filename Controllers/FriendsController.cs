using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Services;
namespace User.Controllers;
[ApiController, Route("api/friends")]
public class FriendsController(MongoDbContext db, ProfilePolicy profiles, SocialCommands commands) : ControllerBase
{
    private string Owner(string id)
    {
        id = SocialCommands.Account(id);
        if (SocialCommands.Account(Input.Actor(User)) != id) throw new ApiProblem(403, "This action belongs to another account.");
        return id;
    }
    [HttpPost("request")]
    public async Task<object> SendRequest(SendFriendRequestDto dto)
    {
        var friend = await commands.Request(Input.Actor(User), dto.TargetUserId);
        return new { message = "Friend request sent", requestId = SocialCommands.PublicId(friend), friendshipId = SocialCommands.PublicId(friend) };
    }
    [HttpPost("{id}/accept")] public async Task<object> Accept(string id) { await commands.Transition(Input.Actor(User), id, FriendStatus.Accepted); return new { message = "Friend request accepted" }; }
    [HttpPost("{id}/decline")] public async Task<object> Decline(string id) { await commands.Transition(Input.Actor(User), id, FriendStatus.Declined); return new { message = "Friend request declined" }; }
    [HttpDelete("{id}")]
    public async Task<object> Remove(string id, bool pendingOnly = false)
    {
        var status = await commands.Remove(Input.Actor(User), id, pendingOnly);
        return new { message = status == FriendStatus.Cancelled ? "Friend request cancelled" : "Friend removed" };
    }
    [HttpGet("pending/{userId}")]
    public async Task<object> Pending(string userId)
    {
        var user = await profiles.Find(Owner(userId));
        var friends = await db.Friends.Find(x => x.FriendId == user.Id && x.Status == FriendStatus.Pending).SortByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Limit(100).ToListAsync();
        var ids = friends.Select(x => x.UserId).ToList(); var senders = (await db.Users.Find(x => ids.Contains(x.Id)).Limit(100).ToListAsync()).ToDictionary(x => x.Id);
        return friends.Where(x => senders.ContainsKey(x.UserId)).Select(x => new { requestId = SocialCommands.PublicId(x), requesterId = senders[x.UserId].IdentityUserId, requesterUsername = senders[x.UserId].UserName, requesterAvatar = senders[x.UserId].AvatarUrl, requestedAt = x.CreatedAt });
    }
    [HttpGet("sent/{userId}")]
    public async Task<object> Sent(string userId)
    {
        var user = await profiles.Find(Owner(userId));
        var friends = await db.Friends.Find(x => x.UserId == user.Id && x.Status == FriendStatus.Pending).SortByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Limit(100).ToListAsync();
        var ids = friends.Select(x => x.FriendId).ToList(); var recipients = (await db.Users.Find(x => ids.Contains(x.Id)).Limit(100).ToListAsync()).ToDictionary(x => x.Id);
        return friends.Where(x => recipients.ContainsKey(x.FriendId)).Select(x => new { requestId = SocialCommands.PublicId(x), addresseeId = recipients[x.FriendId].IdentityUserId, addresseeUsername = recipients[x.FriendId].UserName, addresseeAvatar = recipients[x.FriendId].AvatarUrl, requestedAt = x.CreatedAt });
    }
    [HttpGet("{userId}")]
    public async Task<object> Friends(string userId)
    {
        var user = await profiles.Read(User, Guid.TryParse(userId, out var guid) ? guid.ToString() : userId); var friends = await db.Friends.Find(x => (x.FriendId == user.Id || x.UserId == user.Id) && x.Status == FriendStatus.Accepted).Limit(100).ToListAsync();
        var ids = friends.Select(x => x.FriendId == user.Id ? x.UserId : x.FriendId).ToList(); return await db.Users.Find(x => ids.Contains(x.Id)).Project(x => x.IdentityUserId).ToListAsync();
    }
    [HttpGet("status")]
    public async Task<object> Status(string userId1, string userId2)
    {
        var a = await profiles.Find(Owner(userId1)); var b = await profiles.Find(Guid.TryParse(userId2, out var guid) ? guid.ToString() : userId2);
        var friend = await db.Friends.Find(x => x.PairKey == SocialCommands.Pair(a.Id, b.Id)).FirstOrDefaultAsync();
        if (friend == null) return new { status = "none" };
        return new { status = friend.Status is FriendStatus.Cancelled or FriendStatus.Removed ? "none" : friend.Status.ToString().ToLowerInvariant(), friendshipId = SocialCommands.PublicId(friend), requestId = SocialCommands.PublicId(friend), requesterId = friend.UserId == a.Id ? a.IdentityUserId : b.IdentityUserId, addresseeId = friend.FriendId == a.Id ? a.IdentityUserId : b.IdentityUserId, requestedAt = friend.CreatedAt, respondedAt = friend.RespondedAt, friend.CreatedAt, friend.AcceptedAt };
    }
}
public class SendFriendRequestDto { public string TargetUserId { get; set; } = ""; }
