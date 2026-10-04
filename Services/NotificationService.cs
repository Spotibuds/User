using MongoDB.Driver;
using Microsoft.AspNetCore.SignalR;
using User.Data;
using User.Entities;
using User.Hubs;
namespace User.Services;
public interface INotificationService
{
    Task<Notification> CreateNotificationAsync(string targetUserId, NotificationType type, string title, string message, string? sourceUserId = null, Dictionary<string, object>? data = null, string? actionUrl = null, string? key = null);
    Task<List<Notification>> GetUserNotificationsAsync(string userId, int limit = 50, int skip = 0);
    Task<int> GetUnreadCountAsync(string userId);
    Task MarkAsReadAsync(string notificationId, string userId);
    Task MarkAsHandledAsync(string notificationId, string userId);
    Task MarkAllAsReadAsync(string userId);
    Task CleanupOldNotificationsAsync(string userId, int daysOld = 30);
    Task<Notification> PersistAsync(IClientSessionHandle session, Notification notification, CancellationToken ct);
    Task PublishAsync(Notification notification);
    Task RefreshCountAsync(string userId);
}
public class NotificationService(MongoDbContext db, IHubContext<NotificationHub> hub, TimeProvider clock, NotificationCommands? notificationCommands = null) : INotificationService
{
    private readonly NotificationCommands commands = notificationCommands ?? new(db, hub, clock);
    private readonly MongoTransactions transactions = new(db.Database.Client);
    private DateTime Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()).UtcDateTime;
    private static string Reference(Notification n, string key)
    {
        if (!n.Data.TryGetValue(key, out var value) || value is not string text || string.IsNullOrWhiteSpace(text)) throw new ApiProblem(400, "Notification reference required.");
        return text;
    }
    private static string ObjectReference(Notification n, string key) { var value = Reference(n, key); Input.ObjectId(value); return MongoDB.Bson.ObjectId.Parse(value).ToString(); }
    private async Task Validate(IClientSessionHandle session, Notification n, CancellationToken ct)
    {
        Input.ObjectId(n.Id); n.TargetUserId = SocialCommands.Account(n.TargetUserId);
        if (n.SourceUserId != null) n.SourceUserId = SocialCommands.Account(n.SourceUserId);
        if (!Enum.IsDefined(n.Type) || n.Status != NotificationStatus.Unread || n.DismissedAt != null) throw new ApiProblem(400, "Invalid notification type or initial state.");
        if (n.SourceUserId == n.TargetUserId) throw new ApiProblem(400, "Self notifications are not supported.");
        if (n.Type != NotificationType.Other && n.SourceUserId == null) throw new ApiProblem(400, "Notification actor required.");
        n.Title = Input.Text(n.Title, 200, required: true); n.Message = Input.Text(n.Message, 2000);
        n.Data ??= new();
        if (n.Data.Count > 16 || n.Data.Any(x => x.Key.Length > 80 || x.Value is string s && s.Length > 2000)) throw new ApiProblem(400, "Notification metadata exceeds supported bounds.");
        if (n.Key != null && (string.IsNullOrWhiteSpace(n.Key) || n.Key.Length > 256)) throw new ApiProblem(400, "Invalid notification event key.");
        if (n.ActionUrl != null && (n.ActionUrl.Length > 1024 || !n.ActionUrl.StartsWith('/') || n.ActionUrl.StartsWith("//") || n.ActionUrl.Contains('\\') || n.ActionUrl.Any(char.IsControl))) throw new ApiProblem(400, "Invalid notification link.");
        n.CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(new DateTimeOffset(n.CreatedAt.ToUniversalTime()).ToUnixTimeMilliseconds()).UtcDateTime;
        var profiles = await db.Users.Find(session, x => x.IdentityUserId == n.TargetUserId || x.IdentityUserId == n.SourceUserId).ToListAsync(ct);
        var recipient = profiles.SingleOrDefault(x => x.IdentityUserId == n.TargetUserId) ?? throw new ApiProblem(404, "Notification recipient not found.");
        var sender = n.SourceUserId is null ? null : profiles.SingleOrDefault(x => x.IdentityUserId == n.SourceUserId) ?? throw new ApiProblem(404, "Notification actor not found.");
        if (n.Type == NotificationType.Message)
        {
            var messageId = ObjectReference(n, "messageId"); var chatId = ObjectReference(n, "chatId");
            var message = await db.Messages.Find(session, x => x.Id == messageId && x.ChatId == chatId && x.SenderId == sender!.Id).FirstOrDefaultAsync(ct);
            var chat = await db.Chats.Find(session, x => x.Id == chatId && x.Participants.Contains(sender!.Id) && x.Participants.Contains(recipient.Id)).FirstOrDefaultAsync(ct);
            if (message == null || chat == null) throw new ApiProblem(404, "Notification message or participant not found.");
            if (n.ActionUrl != $"/chat/{chatId}") throw new ApiProblem(400, "Notification message action disagrees.");
        }
        else if (n.Type is NotificationType.FriendRequest or NotificationType.FriendRequestAccepted or NotificationType.FriendRequestDeclined or NotificationType.FriendRemoved)
        {
            var id = ObjectReference(n, "friendshipId");
            var relation = await db.Friends.Find(session, SocialCommands.RequestFilter(id)).FirstOrDefaultAsync(ct);
            var expected = n.Type switch { NotificationType.FriendRequest => FriendStatus.Pending, NotificationType.FriendRequestAccepted => FriendStatus.Accepted, NotificationType.FriendRequestDeclined => FriendStatus.Declined, _ => FriendStatus.Removed };
            var direction = relation != null && (n.Type == NotificationType.FriendRequest ? relation.UserId == sender!.Id && relation.FriendId == recipient.Id : n.Type == NotificationType.FriendRemoved ? (relation.UserId == sender!.Id && relation.FriendId == recipient.Id || relation.FriendId == sender.Id && relation.UserId == recipient.Id) : relation.UserId == recipient.Id && relation.FriendId == sender!.Id);
            if (!direction || relation!.Status != expected) throw new ApiProblem(404, "Notification friendship attempt not found.");
            if (n.Type != NotificationType.FriendRemoved && ObjectReference(n, "requestId") != id) throw new ApiProblem(400, "Notification request references disagree.");
            if (n.ActionUrl != "/friends") throw new ApiProblem(400, "Notification friendship action disagrees.");
            if (n.Type == NotificationType.FriendRequest)
            {
                if (SocialCommands.Account(Reference(n, "requesterId")) != n.SourceUserId) throw new ApiProblem(400, "Notification requester disagrees.");
                n.Data["requesterId"] = n.SourceUserId!; n.Data["requesterUsername"] = sender!.UserName;
                n.ExpiresAt ??= n.CreatedAt.AddDays(30);
            }
            else if (SocialCommands.Account(Reference(n, "friendId")) != n.SourceUserId) throw new ApiProblem(400, "Notification friend actor disagrees.");
        }
        else if (n.Type == NotificationType.Follow)
        {
            if (SocialCommands.Account(Reference(n, "followerId")) != n.SourceUserId || SocialCommands.Account(Reference(n, "followedId")) != n.TargetUserId) throw new ApiProblem(400, "Notification follow references disagree.");
            if (!await db.Follows.Find(session, x => x.FollowerId == n.SourceUserId && x.FollowedId == n.TargetUserId).AnyAsync(ct)) throw new ApiProblem(404, "Notification follow not found.");
            if (n.ActionUrl != $"/user/{(sender!.IsPrivate ? recipient.IdentityUserId : sender.IdentityUserId)}") throw new ApiProblem(400, "Notification follower action disagrees.");
            n.Data["followerId"] = n.SourceUserId!; n.Data["followedId"] = n.TargetUserId;
        }
        else if (n.Type == NotificationType.Reaction)
        {
            var id = ObjectReference(n, "reactionId"); var post = Reference(n, "postId"); var emoji = Reference(n, "emoji");
            var reaction = await db.Reactions.Find(session, x => x.Id == id && x.PostId == post && x.FromIdentityUserId == n.SourceUserId && x.ToIdentityUserId == n.TargetUserId && x.Emoji == emoji).FirstOrDefaultAsync(ct);
            if (reaction == null) throw new ApiProblem(404, "Notification reaction not found.");
            var feed = await ReactionPostReference.Read(db, session, post, n.SourceUserId!, clock, ct, allowPrivate: true);
            if (feed.Owner != n.TargetUserId) throw new ApiProblem(400, "Notification post recipient disagrees.");
            if (Reference(n, "actionPostId") != feed.ActionPostId || n.ActionUrl != $"/feed/post/{Uri.EscapeDataString(feed.ActionPostId)}") throw new ApiProblem(400, "Notification post action disagrees.");
            n.ExpiresAt = feed.ExpiresAt;
        }
        // Serialize producer transactions against account cleanup using the same profile rows.
        foreach (var profile in profiles.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var stamp = Now; if (profile.UpdatedAt >= stamp) stamp = profile.UpdatedAt.Value.AddMilliseconds(1);
            await db.Users.UpdateOneAsync(session, x => x.Id == profile.Id, Builders<Models.User>.Update.Set(x => x.UpdatedAt, stamp), cancellationToken: ct);
        }
    }
    private static Notification Existing(Notification n, Notification candidate)
    {
        if (n.TargetUserId != SocialCommands.Account(candidate.TargetUserId) || n.SourceUserId != (candidate.SourceUserId == null ? null : SocialCommands.Account(candidate.SourceUserId)) || n.Type != candidate.Type) throw new ApiProblem(409, "Notification event key belongs to a different recipient or action.");
        return n;
    }
    public async Task<Notification> PersistAsync(IClientSessionHandle session, Notification n, CancellationToken ct)
    {
        if (n.Key != null)
        {
            var existing = await db.Notifications.Find(session, x => x.Key == n.Key).FirstOrDefaultAsync(ct);
            if (existing != null) return Existing(existing, n);
        }
        await Validate(session, n, ct);
        await db.Notifications.InsertOneAsync(session, n, cancellationToken: ct); return n;
    }
    public Task PublishAsync(Notification n) => commands.Publish(n);
    public Task RefreshCountAsync(string user) => commands.Changed(user);
    public static NotificationDto Dto(Notification n) => NotificationDto.From(n);
    public async Task<Notification> CreateNotificationAsync(string targetUserId, NotificationType type, string title, string message, string? sourceUserId = null, Dictionary<string, object>? data = null, string? actionUrl = null, string? key = null)
    {
        var candidate = new Notification { TargetUserId = targetUserId, SourceUserId = sourceUserId, Type = type, Title = title, Message = message, Data = data ?? new(), ActionUrl = actionUrl, Key = key, CreatedAt = Now, ExpiresAt = type == NotificationType.FriendRequest ? Now.AddDays(30) : null };
        Notification persisted;
        try { persisted = await transactions.Run((session, ct) => PersistAsync(session, candidate, ct)); }
        catch (MongoWriteException ex) when (key != null && ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await db.Notifications.Find(x => x.Key == key).FirstOrDefaultAsync();
            if (existing == null) throw;
            persisted = Existing(existing, candidate);
        }
        if (persisted.Id == candidate.Id) await PublishAsync(persisted);
        return persisted;
    }
    public Task<List<Notification>> GetUserNotificationsAsync(string userId, int limit = 50, int skip = 0)
    {
        userId = SocialCommands.Account(userId); Input.Page(limit, skip);
        return db.Notifications.Find(commands.Active(userId)).SortByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Skip(skip).Limit(limit).ToListAsync();
    }
    public Task<int> GetUnreadCountAsync(string userId) => commands.Unread(userId);
    public async Task MarkAsReadAsync(string id, string user) => await commands.Read(id, user);
    public async Task MarkAsHandledAsync(string id, string user) => await commands.Read(id, user, handled: true);
    public async Task MarkAllAsReadAsync(string user) => await commands.ReadAll(user);
    public async Task CleanupOldNotificationsAsync(string user, int daysOld = 30) => await commands.Cleanup(user, daysOld);
}
