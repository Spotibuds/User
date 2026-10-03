using MongoDB.Driver;
using User.Data;
using User.Entities;
using Microsoft.AspNetCore.SignalR;
using User.Hubs;
namespace User.Services;
public sealed class SocialCommands(MongoDbContext db, ProfilePolicy profiles, INotificationService notifications, IHubContext<FriendHub> hub, MongoTransactions transactions, TimeProvider clock)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string Pair(string a, string b) => string.Join(':', new[] { a, b }.Order(StringComparer.Ordinal));
    private static string RequestKey(Friend f) => $"friend-request:{f.Id}:{f.CreatedAt.Ticks}";
    private Notification Notice(string target, string actor, NotificationType type, string title, string key, Dictionary<string, object>? data = null) => new()
    {
        TargetUserId = target, SourceUserId = actor, Type = type, Title = title, Message = "Open your friends page for the current status.",
        Key = key, Data = data ?? new(), ActionUrl = "/friends", CreatedAt = clock.GetUtcNow().UtcDateTime,
        ExpiresAt = type == NotificationType.FriendRequest ? clock.GetUtcNow().UtcDateTime.AddDays(30) : null
    };
    public async Task<Friend> Request(string actor, string target)
    {
        Input.GuidId(target); if (actor == target) throw new ApiProblem(400, "Cannot friend yourself.");
        var sender = await profiles.Find(actor); var receiver = await profiles.Find(target);
        await Gate.WaitAsync();
        try
        {
            var result = await transactions.Run(async (session, ct) =>
            {
                var key = Pair(sender.Id, receiver.Id);
                var existing = await db.Friends.Find(session, x => x.PairKey == key).FirstOrDefaultAsync(ct);
                if (existing is { Status: FriendStatus.Accepted or FriendStatus.Pending }) return (friend: existing, notice: (Notification?)null);
                var friend = new Friend { UserId = sender.Id, FriendId = receiver.Id, PairKey = key, CreatedAt = clock.GetUtcNow().UtcDateTime };
                if (existing != null) { friend.Id = existing.Id; await db.Friends.ReplaceOneAsync(session, x => x.Id == existing.Id, friend, cancellationToken: ct); }
                else await db.Friends.InsertOneAsync(session, friend, cancellationToken: ct);
                var n = await notifications.PersistAsync(session, Notice(target, actor, NotificationType.FriendRequest, $"Friend request from {sender.UserName}", RequestKey(friend), new() { ["requestId"] = friend.Id, ["friendshipId"] = friend.Id, ["requesterId"] = actor, ["requesterUsername"] = sender.UserName }), ct);
                return (friend, notice: (Notification?)n);
            });
            if (result.notice != null) await notifications.PublishAsync(result.notice);
            if (result.friend.Status == FriendStatus.Pending && result.friend.UserId == sender.Id)
                await hub.Clients.Group($"user_{target}").SendAsync("FriendRequestReceived", new { requestId = result.friend.Id, requesterId = actor, requesterUsername = sender.UserName, requesterAvatar = sender.AvatarUrl, requestedAt = result.friend.CreatedAt });
            return result.friend;
        }
        finally { Gate.Release(); }
    }
    public async Task<Friend> Transition(string actor, string id, FriendStatus status)
    {
        Input.ObjectId(id); var user = await profiles.Find(actor);
        await Gate.WaitAsync();
        try
        {
            var result = await transactions.Run(async (session, ct) =>
            {
                var friend = await db.Friends.Find(session, x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Friend request not found.");
                if (friend.FriendId != user.Id) throw new ApiProblem(403, "Only the recipient can respond.");
                if (friend.Status != FriendStatus.Pending && friend.Status != status) throw new ApiProblem(409, "The request already has a different response.");
                var sender = await db.Users.Find(session, x => x.Id == friend.UserId).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Requester no longer exists.");
                if (friend.Status == FriendStatus.Pending)
                {
                    await db.Friends.UpdateOneAsync(session, x => x.Id == id && x.Status == FriendStatus.Pending, Builders<Friend>.Update.Set(x => x.Status, status).Set(x => x.AcceptedAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
                    await db.Notifications.UpdateManyAsync(session, n => n.TargetUserId == actor && n.Type == NotificationType.FriendRequest && n.SourceUserId == sender.IdentityUserId, Builders<Notification>.Update.Set(n => n.Status, NotificationStatus.Handled).Set(n => n.HandledAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
                }
                var n = await notifications.PersistAsync(session, Notice(sender.IdentityUserId, actor, status == FriendStatus.Accepted ? NotificationType.FriendRequestAccepted : NotificationType.FriendRequestDeclined, $"{user.UserName} {status.ToString().ToLowerInvariant()} your request", $"friend-response:{id}:{friend.CreatedAt.Ticks}", new() { ["friendshipId"] = id }), ct);
                friend.Status = status; return (friend, sender, notice: n);
            });
            await notifications.PublishAsync(result.notice); await notifications.RefreshCountAsync(actor);
            await hub.Clients.Group($"user_{result.sender.IdentityUserId}").SendAsync(status == FriendStatus.Accepted ? "FriendRequestAccepted" : "FriendRequestDeclined", new { friendshipId = id, friendId = actor, friendName = user.UserName });
            return result.friend;
        }
        finally { Gate.Release(); }
    }
    public async Task Remove(string actor, string id)
    {
        Input.ObjectId(id); var user = await profiles.Find(actor);
        var result = await transactions.Run(async (session, ct) =>
        {
            var f = await db.Friends.Find(session, x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Friendship not found.");
            if (f.UserId != user.Id && f.FriendId != user.Id) throw new ApiProblem(403, "Friendship belongs to other users.");
            var other = await db.Users.Find(session, x => x.Id == (f.UserId == user.Id ? f.FriendId : f.UserId)).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Other account no longer exists.");
            await db.Friends.DeleteOneAsync(session, x => x.Id == id, cancellationToken: ct);
            var n = await notifications.PersistAsync(session, Notice(other.IdentityUserId, actor, NotificationType.FriendRemoved, "Friend removed", $"friend-removed:{id}:{f.CreatedAt.Ticks}"), ct);
            return (other, notice: n);
        });
        await notifications.PublishAsync(result.notice);
        await hub.Clients.Group($"user_{result.other.IdentityUserId}").SendAsync("FriendRemoved", new { friendId = actor, friendshipId = id });
    }
    public async Task Follow(string actor, string target, bool follow)
    {
        Input.GuidId(target); if (actor == target) throw new ApiProblem(400, "Cannot follow yourself.");
        await profiles.Find(actor); await profiles.Find(target);
        if (follow) await db.Follows.UpdateOneAsync(e => e.FollowerId == actor && e.FollowedId == target, Builders<FollowEdge>.Update.SetOnInsert(e => e.FollowerId, actor).SetOnInsert(e => e.FollowedId, target), new UpdateOptions { IsUpsert = true });
        else await db.Follows.DeleteOneAsync(e => e.FollowerId == actor && e.FollowedId == target);
    }
}
