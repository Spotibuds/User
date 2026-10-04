using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Hubs;
namespace User.Services;
public sealed class ChatCommands(MongoDbContext db, ProfilePolicy profiles, IHubContext<ChatHub> hub, IHubContext<FriendHub> friendHub, INotificationService notifications, IActiveChatTrackingService active, MongoTransactions transactions, TimeProvider clock, ILogger<ChatCommands> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private DateTime Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()).UtcDateTime;
    public async Task<Chat> Member(string actor, string id, CancellationToken ct = default)
    {
        Input.ObjectId(id); var user = await profiles.Find(actor, ct);
        var chat = await db.Chats.Find(x => x.Id == id).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Chat not found.");
        if (!chat.Participants.Contains(user.Id)) throw new ApiProblem(403, "You are not a participant in this chat.");
        return chat;
    }
    public async Task<Chat> Create(string actor, List<string>? participants, bool group, string? name, CancellationToken ct = default)
    {
        if (participants == null || participants.Count is < 2 or > 20 || !group && participants.Count != 2) throw new ApiProblem(400, "A chat requires between two and twenty participants.");
        var ids = participants.Select(id => Guid.TryParse(id, out var guid) ? guid.ToString() : throw new ApiProblem(400, "Invalid account identifier.")).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new ApiProblem(400, "Chat participants must be distinct.");
        if (!ids.Contains(actor)) throw new ApiProblem(403, "A chat must include yourself.");
        var users = await db.Users.Find(x => ids.Contains(x.IdentityUserId)).ToListAsync(ct);
        if (users.Count != ids.Count) throw new ApiProblem(404, "A participant profile is missing.");
        var self = users.Single(x => x.IdentityUserId == actor);
        var chat = new Chat { Participants = users.Select(x => x.Id).Order(StringComparer.Ordinal).ToList(), IsGroup = group, Name = Input.Text(name, 100), CreatedAt = Now, LastActivity = Now };
        if (!group) chat.DirectKey = string.Join(':', chat.Participants);
        await Gate.WaitAsync(ct);
        try
        {
            if (!group)
            {
                var existing = await db.Chats.Find(x => x.DirectKey == chat.DirectKey).FirstOrDefaultAsync(ct);
                if (existing != null) return existing;
            }
            try
            {
                return await transactions.Run(async (session, token) =>
                {
                    if (!group)
                    {
                        var existing = await db.Chats.Find(session, x => x.DirectKey == chat.DirectKey).FirstOrDefaultAsync(token);
                        if (existing != null) return existing;
                    }
                    var currentUsers = await db.Users.Find(session, x => chat.Participants.Contains(x.Id)).ToListAsync(token);
                    if (currentUsers.Count != ids.Count) throw new ApiProblem(404, "A participant profile is missing.");
                    foreach (var participant in currentUsers)
                    {
                        // Protect the membership/privacy snapshot from profile deletion
                        // and privacy changes that otherwise cannot see this new chat.
                        var touchedAt = participant.UpdatedAt is DateTime previous && previous >= Now ? previous.AddMilliseconds(1) : Now;
                        var updated = await db.Users.UpdateOneAsync(session, x => x.Id == participant.Id && x.IdentityUserId == participant.IdentityUserId, Builders<Models.User>.Update.Set(x => x.UpdatedAt, touchedAt), cancellationToken: token);
                        if (updated.MatchedCount != 1) throw new ApiProblem(404, "A participant profile is missing.");
                    }
                    foreach (var other in currentUsers.Where(x => x.IsPrivate && x.Id != self.Id))
                    {
                        var pair = SocialCommands.Pair(self.Id, other.Id);
                        var relation = await db.Friends.Find(session, x => x.PairKey == pair && x.Status == FriendStatus.Accepted).FirstOrDefaultAsync(token) ?? throw new ApiProblem(403, "Private accounts can be messaged by accepted friends.");
                        // A real write serializes the permission check against removal.
                        // Always change the value, including under a fixed or regressing clock.
                        var touchedAt = relation.UpdatedAt is DateTime previous && previous >= Now ? previous.AddMilliseconds(1) : Now;
                        var updated = await db.Friends.UpdateOneAsync(session, x => x.Id == relation.Id && x.Status == FriendStatus.Accepted, Builders<Friend>.Update.Set(x => x.UpdatedAt, touchedAt), cancellationToken: token);
                        if (updated.MatchedCount != 1) throw new ApiProblem(403, "Private accounts can be messaged by accepted friends.");
                    }
                    await db.Chats.InsertOneAsync(session, chat, cancellationToken: token);
                    return chat;
                }, ct);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                if (group) throw;
                var winner = await db.Chats.Find(x => x.DirectKey == chat.DirectKey).FirstOrDefaultAsync(ct);
                if (winner == null) throw;
                return winner;
            }
        }
        finally { Gate.Release(); }
    }
    public static object MessageDto(Message message, Models.User sender) => new { id = message.Id, messageId = message.Id, chatId = message.ChatId, senderId = sender.IdentityUserId, senderName = sender.UserName, senderUsername = sender.UserName, senderAvatar = sender.AvatarUrl, content = message.Content, type = message.Type.ToString(), sentAt = message.SentAt, createdAt = message.SentAt, timestamp = message.SentAt, isRead = message.ReadBy.Any(r => r.UserId != sender.IdentityUserId), isEdited = message.IsEdited, message.EditedAt, readBy = message.ReadBy, message.ReplyToId, message.ClientMessageId };
    public async Task<object> ChatDto(Chat chat) => (await ChatDtos([chat])).Single();
    public async Task<List<object>> ChatDtos(List<Chat> chats)
    {
        var participantIds = chats.SelectMany(x => x.Participants).Distinct().ToList();
        var messageIds = chats.Where(x => x.LastMessageId != null).Select(x => x.LastMessageId!).ToList();
        var users = (await db.Users.Find(x => participantIds.Contains(x.Id)).ToListAsync()).ToDictionary(x => x.Id);
        var messages = (await db.Messages.Find(x => messageIds.Contains(x.Id)).ToListAsync()).ToDictionary(x => x.Id);
        return chats.Select(chat =>
        {
            var last = chat.LastMessageId != null && messages.TryGetValue(chat.LastMessageId, out var message) ? message : null;
            return (object)new { id = chat.Id, chatId = chat.Id, chat.IsGroup, chat.Name, participants = chat.Participants.Where(users.ContainsKey).Select(x => users[x].IdentityUserId).ToList(), chat.LastActivity, chat.LastMessageId, lastMessageContent = last?.Content, lastMessageSenderId = last != null && users.TryGetValue(last.SenderId, out var sender) ? sender.IdentityUserId : null, chat.CreatedAt };
        }).ToList();
    }
    public static FilterDefinition<Message> AtOrBefore(Message boundary) => Builders<Message>.Filter.Lt(x => x.SentAt, boundary.SentAt) |
        (Builders<Message>.Filter.Eq(x => x.SentAt, boundary.SentAt) & Builders<Message>.Filter.Lte(x => x.Id, boundary.Id));
    public async Task<object> Send(string actor, string id, string content, string? clientMessageId = null, string? replyToId = null, CancellationToken cancellationToken = default)
    {
        content = Input.Text(content, 4000, true);
        if (!Guid.TryParse(clientMessageId ?? Guid.NewGuid().ToString(), out var clientGuid)) throw new ApiProblem(400, "Invalid client message identifier.");
        clientMessageId = clientGuid.ToString();
        var chat = await Member(actor, id, cancellationToken); var sender = await profiles.Find(actor, cancellationToken);
        if (replyToId != null) { Input.ObjectId(replyToId); replyToId = ObjectId.Parse(replyToId).ToString(); }
        // Recognize older clients' valid alternative GUID formats as the same retry key.
        var formats = new[] { "D", "N", "B", "P" }.Select(format => Regex.Escape(clientGuid.ToString(format)));
        var nonce = Builders<Message>.Filter.Regex(x => x.ClientMessageId, new BsonRegularExpression("^(?:" + string.Join('|', formats) + ")$", "i"));
        List<Models.User> participants;
        (Message message, List<Notification> notices, bool inserted) result;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            participants = await db.Users.Find(x => chat.Participants.Contains(x.Id)).ToListAsync(cancellationToken);
            result = await transactions.Run(async (session, ct) =>
            {
                var currentChat = await db.Chats.Find(session, x => x.Id == id && x.Participants.Contains(sender.Id)).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Chat no longer exists.");
                var message = await db.Messages.Find(session, Builders<Message>.Filter.Where(x => x.ChatId == id && x.SenderId == sender.Id) & nonce).FirstOrDefaultAsync(ct);
                if (message != null)
                {
                    if (message.Content != content || message.ReplyToId != replyToId) throw new ApiProblem(409, "Message identifier was already used for different content.");
                    return (message, new List<Notification>(), false);
                }
                if (replyToId != null && !await db.Messages.Find(session, x => x.Id == replyToId && x.ChatId == id).AnyAsync(ct)) throw new ApiProblem(400, "Reply message belongs to a different chat.");
                // Preserve timeline order even if the system clock moves backwards.
                var sentAt = Now < currentChat.LastActivity ? currentChat.LastActivity : Now;
                message = new Message { ChatId = id, SenderId = sender.Id, Content = content, ClientMessageId = clientMessageId, ReplyToId = replyToId, CreatedAt = sentAt, SentAt = sentAt, ReadBy = [new MessageRead { UserId = actor, ReadAt = sentAt }] };
                await db.Messages.InsertOneAsync(session, message, cancellationToken: ct);
                await db.Chats.UpdateOneAsync(session, x => x.Id == id, Builders<Chat>.Update.Set(x => x.LastMessageId, message.Id).Set(x => x.LastActivity, sentAt), cancellationToken: ct);
                var notices = new List<Notification>();
                foreach (var participant in participants.Where(x => x.IdentityUserId != actor && !active.IsUserInChat(id, x.IdentityUserId)))
                    notices.Add(await notifications.PersistAsync(session, new Notification { TargetUserId = participant.IdentityUserId, SourceUserId = actor, Type = NotificationType.Message, Title = $"New message from {sender.UserName}", Message = content[..Math.Min(content.Length, 80)], Data = new() { ["chatId"] = id, ["messageId"] = message.Id }, ActionUrl = $"/chat/{id}", Key = $"message:{message.Id}:{participant.IdentityUserId}", CreatedAt = sentAt }, ct));
                return (message, notices, true);
            }, cancellationToken);
        }
        finally { Gate.Release(); }
        var dto = MessageDto(result.message, sender);
        if (result.inserted)
        {
            await PublishCommitted(async ct =>
            {
                var sends = participants.SelectMany(participant => new Func<Task>[]
                {
                    () => hub.Clients.Group($"chat_user_{participant.IdentityUserId}").SendAsync("ReceiveMessage", dto, ct),
                    () => friendHub.Clients.Group($"user_{participant.IdentityUserId}").SendAsync("NewMessage", dto, ct)
                }).Concat(result.notices.Select(n => (Func<Task>)(() => notifications.PublishAsync(n).WaitAsync(ct))));
                await Task.WhenAll(sends.Select(async send => await send()));
            });
        }
        return dto;
    }
    private async Task PublishCommitted(Func<CancellationToken, Task> publish)
    {
        // Persistence is the send acknowledgement. A broken live transport must not
        // turn a committed message/receipt into an apparent failed write.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await publish(timeout.Token); }
        catch (Exception ex) { logger.LogWarning("Committed chat event could not be published ({ErrorType}). Clients recover through history.", ex.GetType().Name); }
    }
    private async Task PublishReceipt(Chat chat, string eventName, object payload)
    {
        await PublishCommitted(async ct =>
        {
            var users = await db.Users.Find(x => chat.Participants.Contains(x.Id)).Project(x => x.IdentityUserId).ToListAsync(ct);
            var sends = users.SelectMany(user => new Func<Task>[]
            {
                () => hub.Clients.Group($"chat_user_{user}").SendAsync(eventName, payload, ct),
                () => friendHub.Clients.Group($"user_{user}").SendAsync(eventName, payload, ct)
            });
            await Task.WhenAll(sends.Select(async send => await send()));
        });
    }
    private static FilterDefinition<Notification> MessageNotices(string actor, string chatId) => Builders<Notification>.Filter.Where(x => x.TargetUserId == actor && x.Type == NotificationType.Message && x.Status == NotificationStatus.Unread) & Builders<Notification>.Filter.Eq("Data.chatId", chatId);
    public async Task Read(string actor, string messageId, CancellationToken cancellationToken = default)
    {
        Input.ObjectId(messageId); messageId = ObjectId.Parse(messageId).ToString();
        var message = await db.Messages.Find(x => x.Id == messageId).FirstOrDefaultAsync(cancellationToken) ?? throw new ApiProblem(404, "Message not found.");
        var chat = await Member(actor, message.ChatId, cancellationToken);
        var self = await profiles.Find(actor, cancellationToken); var readAt = Now < message.SentAt ? message.SentAt : Now;
        var changed = await transactions.Run(async (session, ct) =>
        {
            if (!await db.Chats.Find(session, x => x.Id == chat.Id && x.Participants.Contains(self.Id)).AnyAsync(ct)) throw new ApiProblem(404, "Chat no longer exists.");
            var receipt = await db.Messages.UpdateOneAsync(session, x => x.Id == messageId && !x.ReadBy.Any(r => r.UserId == actor), Builders<Message>.Update.Push(x => x.ReadBy, new MessageRead { UserId = actor, ReadAt = readAt }), cancellationToken: ct);
            await db.Notifications.UpdateManyAsync(session, MessageNotices(actor, chat.Id) & Builders<Notification>.Filter.Eq("Data.messageId", messageId), Builders<Notification>.Update.Set(x => x.Status, NotificationStatus.Read).Set(x => x.ReadAt, readAt), cancellationToken: ct);
            return receipt.ModifiedCount > 0;
        }, cancellationToken);
        if (changed) await PublishReceipt(chat, "MessageRead", new { chatId = chat.Id, messageId, userId = actor, readAt });
        await PublishCommitted(ct => notifications.RefreshCountAsync(actor).WaitAsync(ct));
    }
    public async Task<object> ReadAll(string actor, string chatId, string? throughMessageId = null, CancellationToken cancellationToken = default)
    {
        var chat = await Member(actor, chatId, cancellationToken); var self = await profiles.Find(actor, cancellationToken);
        if (throughMessageId != null) { Input.ObjectId(throughMessageId); throughMessageId = ObjectId.Parse(throughMessageId).ToString(); }
        var readAt = Now;
        var result = await transactions.Run(async (session, ct) =>
        {
            if (!await db.Chats.Find(session, x => x.Id == chat.Id && x.Participants.Contains(self.Id)).AnyAsync(ct)) throw new ApiProblem(404, "Chat no longer exists.");
            var boundary = throughMessageId == null
                ? await db.Messages.Find(session, x => x.ChatId == chatId).SortByDescending(x => x.SentAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct)
                : await db.Messages.Find(session, x => x.Id == throughMessageId && x.ChatId == chatId).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(400, "Read boundary belongs to a different chat.");
            if (boundary == null) return (boundary, false);
            if (readAt < boundary.SentAt) readAt = boundary.SentAt;
            var receipt = await db.Messages.UpdateManyAsync(session, Builders<Message>.Filter.Where(x => x.ChatId == chatId && !x.ReadBy.Any(r => r.UserId == actor)) & AtOrBefore(boundary), Builders<Message>.Update.Push(x => x.ReadBy, new MessageRead { UserId = actor, ReadAt = readAt }), cancellationToken: ct);
            var pending = await db.Notifications.Find(session, MessageNotices(actor, chatId)).ToListAsync(ct);
            var noticeMessageIds = pending.Where(n => n.Data.TryGetValue("messageId", out var value) && value is string).Select(n => (string)n.Data["messageId"]).ToList();
            var readIds = await db.Messages.Find(session, Builders<Message>.Filter.Where(x => x.ChatId == chatId && noticeMessageIds.Contains(x.Id)) & AtOrBefore(boundary)).Project(x => x.Id).ToListAsync(ct);
            await db.Notifications.UpdateManyAsync(session, MessageNotices(actor, chatId) & Builders<Notification>.Filter.In("Data.messageId", readIds), Builders<Notification>.Update.Set(x => x.Status, NotificationStatus.Read).Set(x => x.ReadAt, readAt), cancellationToken: ct);
            return (boundary, receipt.ModifiedCount > 0);
        }, cancellationToken);
        var payload = new { chatId, userId = actor, readAt, throughMessageId = result.boundary?.Id, throughSentAt = result.boundary?.SentAt };
        if (result.Item2) await PublishReceipt(chat, "AllMessagesRead", payload);
        await PublishCommitted(ct => notifications.RefreshCountAsync(actor).WaitAsync(ct));
        return payload;
    }
}
