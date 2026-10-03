using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Services;
namespace User.Hubs;
[Authorize]
public class FriendHub(MongoDbContext db, SocialCommands social, ChatCommands chat, ProfilePolicy profiles, PresenceStore presence) : Hub
{
    private string Actor => Input.Actor(Context.User!);
    public override async Task OnConnectedAsync() { await profiles.Find(Actor); await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{Actor}"); if (presence.Add(Actor, Context.ConnectionId)) await Status(true); await GetOnlineFriends(); await base.OnConnectedAsync(); }
    public override async Task OnDisconnectedAsync(Exception? exception) { if (presence.Remove(Actor, Context.ConnectionId)) await Status(false); await base.OnDisconnectedAsync(exception); }
    private async Task Status(bool online)
    {
        var self = await profiles.Find(Actor); var friends = await db.Friends.Find(x => (x.UserId == self.Id || x.FriendId == self.Id) && x.Status == FriendStatus.Accepted).Limit(100).ToListAsync();
        var ids = friends.Select(x => x.UserId == self.Id ? x.FriendId : x.UserId).ToList(); var users = await db.Users.Find(x => ids.Contains(x.Id)).ToListAsync();
        foreach (var user in users) await Clients.Group($"user_{user.IdentityUserId}").SendAsync("FriendStatusChanged", new { friendId = Actor, friendName = self.UserName, isOnline = online, timestamp = DateTime.UtcNow });
    }
    public async Task GetOnlineFriends() { var self = await profiles.Find(Actor); var friendships = await db.Friends.Find(x => (x.UserId == self.Id || x.FriendId == self.Id) && x.Status == FriendStatus.Accepted).Limit(100).ToListAsync(); var ids = friendships.Select(x => x.UserId == self.Id ? x.FriendId : x.UserId).ToList(); var users = await db.Users.Find(x => ids.Contains(x.Id)).ToListAsync(); await Clients.Caller.SendAsync("OnlineFriends", users.Select(x => x.IdentityUserId).Where(presence.Online).ToList()); }
    public async Task SendFriendRequest(string targetUserId) => await social.Request(Actor, targetUserId);
    public async Task AcceptFriendRequest(string requestId) => await social.Transition(Actor, requestId, FriendStatus.Accepted);
    public async Task DeclineFriendRequest(string requestId) => await social.Transition(Actor, requestId, FriendStatus.Declined);
    public async Task RemoveFriend(string friendId)
    {
        var self = await profiles.Find(Actor); var other = await profiles.Find(friendId); var relation = await db.Friends.Find(x => x.PairKey == SocialCommands.Pair(self.Id, other.Id)).FirstOrDefaultAsync() ?? throw new ApiProblem(404, "Friendship not found."); await social.Remove(Actor, relation.Id);
    }
    public async Task<object> SendMessage(string chatId, string message, string? clientMessageId = null) => await chat.Send(Actor, chatId, message, clientMessageId);
    public async Task MarkMessageAsRead(string messageId) => await chat.Read(Actor, messageId);
    public async Task CreateChat(string friendId) { var result = await chat.Create(Actor, [Actor, friendId], false, null); await Clients.Caller.SendAsync("ChatCreated", await chat.ChatDto(result)); }
}
