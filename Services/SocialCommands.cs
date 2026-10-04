using MongoDB.Driver;
using User.Data;
using User.Entities;
using Microsoft.AspNetCore.SignalR;
using User.Hubs;
namespace User.Services;

public sealed class SocialCommands(MongoDbContext db, ProfilePolicy profiles, INotificationService notifications, IHubContext<FriendHub> hub, MongoTransactions transactions, TimeProvider clock, ILogger<SocialCommands> logger)
{
    public static string Account(string id) { Input.GuidId(id); return Guid.Parse(id).ToString(); }
    public static string Pair(string a, string b) => string.Join(':', new[] { a, b }.Order(StringComparer.Ordinal));
    public static string PublicId(Friend friend) => friend.RequestId ?? friend.Id;
    public static FilterDefinition<Friend> RequestFilter(string id)
    {
        Input.ObjectId(id);
        id = MongoDB.Bson.ObjectId.Parse(id).ToString();
        return Builders<Friend>.Filter.Eq(x => x.RequestId, id) |
            (Builders<Friend>.Filter.Eq(x => x.RequestId, null) & Builders<Friend>.Filter.Eq(x => x.Id, id));
    }
    private static string RequestKey(Friend friend) => $"friend-request:{PublicId(friend)}";
    private Notification Notice(string target, string actor, NotificationType type, string title, string key, Dictionary<string, object>? data = null) => new()
    {
        TargetUserId = target, SourceUserId = actor, Type = type, Title = title, Message = "Open your friends page for the current status.",
        Key = key, Data = data ?? new(), ActionUrl = "/friends", CreatedAt = clock.GetUtcNow().UtcDateTime,
        ExpiresAt = type == NotificationType.FriendRequest ? clock.GetUtcNow().UtcDateTime.AddDays(30) : null
    };
    private Task HandleRequest(IClientSessionHandle session, Friend friend, string recipient, string sender, CancellationToken ct)
    {
        var filter = Builders<Notification>.Filter.Where(n => n.TargetUserId == recipient && n.SourceUserId == sender && n.Type == NotificationType.FriendRequest) &
            (Builders<Notification>.Filter.Eq(n => n.Key, RequestKey(friend)) | Builders<Notification>.Filter.Eq("Data.requestId", PublicId(friend)));
        return db.Notifications.UpdateManyAsync(session, filter, Builders<Notification>.Update.Set(n => n.Status, NotificationStatus.Handled).Set(n => n.HandledAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
    }
    private async Task PublishCommitted(Func<CancellationToken, Task> publish)
    {
        // A live transport failure cannot undo the transaction or its acknowledgement.
        // Clients recover the authoritative state from the persisted lists/notifications.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await publish(timeout.Token); }
        catch (Exception ex) { logger.LogWarning("Committed friend event could not be published ({ErrorType}). Clients recover through persisted state.", ex.GetType().Name); }
    }
    private async Task PublishTransition(string name, Friend friend, Models.User sender, Models.User recipient)
    {
        var id = PublicId(friend);
        await PublishCommitted(ct => hub.Clients.Group($"user_{sender.IdentityUserId}").SendAsync(name, new { requestId = id, friendshipId = id, friendId = recipient.IdentityUserId, friendName = recipient.UserName }, ct));
        await PublishCommitted(ct => hub.Clients.Group($"user_{recipient.IdentityUserId}").SendAsync(name, new { requestId = id, friendshipId = id, friendId = sender.IdentityUserId, friendName = sender.UserName }, ct));
    }
    public async Task<Friend> Request(string actor, string target)
    {
        actor = Account(actor); target = Account(target);
        if (actor == target) throw new ApiProblem(400, "Cannot friend yourself.");
        var sender = await profiles.Find(actor); var receiver = await profiles.Find(target);
        var result = await transactions.Run(async (session, ct) =>
        {
            var key = Pair(sender.Id, receiver.Id);
            var existing = await db.Friends.Find(session, x => x.PairKey == key).FirstOrDefaultAsync(ct);
            if (existing?.Status == FriendStatus.Accepted) throw new ApiProblem(409, "You are already friends.");
            if (existing?.Status == FriendStatus.Pending) throw new ApiProblem(409, existing.UserId == sender.Id ? "A friend request is already pending." : "Respond to the incoming friend request.");
            if (existing?.Status == FriendStatus.Blocked) throw new ApiProblem(409, "This relationship is blocked.");
            var friend = new Friend { UserId = sender.Id, FriendId = receiver.Id, PairKey = key, CreatedAt = clock.GetUtcNow().UtcDateTime };
            friend.RequestId = friend.Id;
            if (existing != null) { friend.Id = existing.Id; await db.Friends.ReplaceOneAsync(session, x => x.Id == existing.Id, friend, cancellationToken: ct); }
            else await db.Friends.InsertOneAsync(session, friend, cancellationToken: ct);
            var id = PublicId(friend);
            var n = await notifications.PersistAsync(session, Notice(target, actor, NotificationType.FriendRequest, $"Friend request from {sender.UserName}", RequestKey(friend), new() { ["requestId"] = id, ["friendshipId"] = id, ["requesterId"] = actor, ["requesterUsername"] = sender.UserName }), ct);
            return (friend, notice: n);
        });
        await PublishCommitted(ct => notifications.PublishAsync(result.notice).WaitAsync(ct));
        var payload = new { requestId = PublicId(result.friend), friendshipId = PublicId(result.friend), requesterId = actor, requesterUsername = sender.UserName, requesterAvatar = sender.AvatarUrl, addresseeId = target, addresseeUsername = receiver.UserName, addresseeAvatar = receiver.AvatarUrl, requestedAt = result.friend.CreatedAt };
        await PublishCommitted(ct => hub.Clients.Group($"user_{target}").SendAsync("FriendRequestReceived", payload, ct));
        await PublishCommitted(ct => hub.Clients.Group($"user_{actor}").SendAsync("FriendRequestSent", payload, ct));
        return result.friend;
    }
    public async Task<Friend> Transition(string actor, string id, FriendStatus status)
    {
        if (status is not (FriendStatus.Accepted or FriendStatus.Declined)) throw new ApiProblem(400, "Invalid friend response.");
        actor = Account(actor); var filter = RequestFilter(id); var user = await profiles.Find(actor);
        var result = await transactions.Run(async (session, ct) =>
        {
            var friend = await db.Friends.Find(session, filter).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Friend request not found.");
            if (friend.FriendId != user.Id) throw new ApiProblem(403, "Only the recipient can respond.");
            if (friend.Status != FriendStatus.Pending && friend.Status != status) throw new ApiProblem(409, "The request already has a different response.");
            var sender = await db.Users.Find(session, x => x.Id == friend.UserId).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Requester no longer exists.");
            if (friend.Status == status) return (friend, sender, notice: (Notification?)null);
            friend.Status = status; friend.RespondedAt = clock.GetUtcNow().UtcDateTime; friend.AcceptedAt = status == FriendStatus.Accepted ? friend.RespondedAt : null;
            await db.Friends.ReplaceOneAsync(session, filter, friend, cancellationToken: ct);
            await HandleRequest(session, friend, actor, sender.IdentityUserId, ct);
            var publicId = PublicId(friend);
            var n = await notifications.PersistAsync(session, Notice(sender.IdentityUserId, actor, status == FriendStatus.Accepted ? NotificationType.FriendRequestAccepted : NotificationType.FriendRequestDeclined, $"{user.UserName} {status.ToString().ToLowerInvariant()} your request", $"friend-response:{publicId}", new() { ["requestId"] = publicId, ["friendshipId"] = publicId, ["friendId"] = actor }), ct);
            return (friend, sender, notice: (Notification?)n);
        });
        if (result.notice != null)
        {
            await PublishCommitted(ct => notifications.PublishAsync(result.notice).WaitAsync(ct));
            await PublishCommitted(ct => notifications.RefreshCountAsync(actor).WaitAsync(ct));
            await PublishTransition(status == FriendStatus.Accepted ? "FriendRequestAccepted" : "FriendRequestDeclined", result.friend, result.sender, user);
        }
        return result.friend;
    }
    public async Task<FriendStatus> Remove(string actor, string id, bool pendingOnly = false)
    {
        actor = Account(actor); var filter = RequestFilter(id); var user = await profiles.Find(actor);
        var result = await transactions.Run(async (session, ct) =>
        {
            var friend = await db.Friends.Find(session, filter).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Friendship not found.");
            if (friend.UserId != user.Id && friend.FriendId != user.Id) throw new ApiProblem(403, "Friendship belongs to other users.");
            if ((friend.Status is FriendStatus.Pending or FriendStatus.Cancelled) && friend.UserId != user.Id) throw new ApiProblem(403, "Only the sender can cancel; decline incoming requests instead.");
            if (pendingOnly && friend.Status is not (FriendStatus.Pending or FriendStatus.Cancelled)) throw new ApiProblem(409, "Only a pending request can be cancelled.");
            if (friend.Status is not (FriendStatus.Pending or FriendStatus.Accepted or FriendStatus.Cancelled or FriendStatus.Removed)) throw new ApiProblem(409, "There is no pending request or accepted friendship to remove.");
            var sender = await db.Users.Find(session, x => x.Id == friend.UserId).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Requester no longer exists.");
            var recipient = await db.Users.Find(session, x => x.Id == friend.FriendId).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Recipient no longer exists.");
            if (friend.Status is FriendStatus.Cancelled or FriendStatus.Removed) return (friend, sender, recipient, changed: false, notice: (Notification?)null);
            var cancellation = friend.Status == FriendStatus.Pending;
            friend.Status = cancellation ? FriendStatus.Cancelled : FriendStatus.Removed; friend.RespondedAt = clock.GetUtcNow().UtcDateTime;
            await db.Friends.ReplaceOneAsync(session, filter, friend, cancellationToken: ct);
            await HandleRequest(session, friend, recipient.IdentityUserId, sender.IdentityUserId, ct);
            var other = sender.Id == user.Id ? recipient : sender;
            var notice = cancellation ? null : await notifications.PersistAsync(session, Notice(other.IdentityUserId, actor, NotificationType.FriendRemoved, "Friend removed", $"friend-removed:{PublicId(friend)}", new() { ["friendshipId"] = PublicId(friend), ["friendId"] = actor }), ct);
            return (friend, sender, recipient, changed: true, notice);
        });
        if (result.changed)
        {
            if (result.notice != null) await PublishCommitted(ct => notifications.PublishAsync(result.notice).WaitAsync(ct));
            await PublishCommitted(ct => notifications.RefreshCountAsync(result.recipient.IdentityUserId).WaitAsync(ct));
            await PublishTransition(result.friend.Status == FriendStatus.Cancelled ? "FriendRequestCancelled" : "FriendRemoved", result.friend, result.sender, result.recipient);
        }
        return result.friend.Status;
    }
    public async Task Follow(string actor, string target, bool follow)
    {
        actor = Account(actor); target = Account(target);
        if (actor == target) throw new ApiProblem(400, "Cannot follow yourself.");
        await profiles.Find(actor); await profiles.Find(target);
        if (follow) await db.Follows.UpdateOneAsync(e => e.FollowerId == actor && e.FollowedId == target, Builders<FollowEdge>.Update.SetOnInsert(e => e.FollowerId, actor).SetOnInsert(e => e.FollowedId, target), new UpdateOptions { IsUpsert = true });
        else await db.Follows.DeleteOneAsync(e => e.FollowerId == actor && e.FollowedId == target);
    }
}
