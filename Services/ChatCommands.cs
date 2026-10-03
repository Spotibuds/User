using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Hubs;
namespace User.Services;
public sealed class ChatCommands(MongoDbContext db, ProfilePolicy profiles, IHubContext<ChatHub> hub, IHubContext<FriendHub> friendHub, INotificationService notifications, IActiveChatTrackingService active, MongoTransactions transactions, TimeProvider clock)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public async Task<Chat> Member(string actor, string id)
    {
        Input.ObjectId(id); var user = await profiles.Find(actor);
        var chat = await db.Chats.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiProblem(404, "Chat not found.");
        if (!chat.Participants.Contains(user.Id)) throw new ApiProblem(403, "You are not a participant in this chat.");
        return chat;
    }
    public async Task<Chat> Create(string actor, List<string> participants, bool group, string? name)
    {
        if (participants.Count is < 2 or > 20 || participants.Distinct().Count() != participants.Count || !participants.Contains(actor) || !group && participants.Count != 2) throw new ApiProblem(403, "A chat requires distinct participants including yourself.");
        foreach (var id in participants) Input.GuidId(id);
        var users = await db.Users.Find(x => participants.Contains(x.IdentityUserId)).ToListAsync();
        if (users.Count != participants.Count) throw new ApiProblem(404, "A participant profile is missing.");
        var self = users.Single(x => x.IdentityUserId == actor);
        foreach (var other in users.Where(x => x.IsPrivate && x.Id != self.Id))
            if (!await db.Friends.Find(x => x.PairKey == SocialCommands.Pair(self.Id, other.Id) && x.Status == FriendStatus.Accepted).AnyAsync()) throw new ApiProblem(403, "Private accounts can be messaged by accepted friends.");
        var chat = new Chat { Participants = users.Select(x => x.Id).Order(StringComparer.Ordinal).ToList(), IsGroup = group, Name = Input.Text(name, 100), CreatedAt = clock.GetUtcNow().UtcDateTime, LastActivity = clock.GetUtcNow().UtcDateTime };
        if (group) { await db.Chats.InsertOneAsync(chat); return chat; }
        chat.DirectKey = string.Join(':', chat.Participants);
        await Gate.WaitAsync();
        try { var existing = await db.Chats.Find(x => x.DirectKey == chat.DirectKey).FirstOrDefaultAsync(); if (existing != null) return existing; await db.Chats.InsertOneAsync(chat); return chat; }
        finally { Gate.Release(); }
    }
    public static object MessageDto(Message message, Models.User sender) => new { id = message.Id, messageId = message.Id, chatId = message.ChatId, senderId = sender.IdentityUserId, senderName = sender.UserName, senderUsername = sender.UserName, senderAvatar = sender.AvatarUrl, content = message.Content, type = message.Type.ToString(), sentAt = message.SentAt, createdAt = message.SentAt, timestamp = message.SentAt, isRead = message.ReadBy.Any(r => r.UserId != sender.IdentityUserId), isEdited = message.IsEdited, message.EditedAt, readBy = message.ReadBy, message.ReplyToId, message.ClientMessageId };
    public async Task<object> ChatDto(Chat chat)
    {
        return (await ChatDtos([chat])).Single();
    }
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
    public async Task<object> Send(string actor, string id, string content, string? clientMessageId = null, string? replyToId = null)
    {
        content = Input.Text(content, 4000, true);
        clientMessageId ??= Guid.NewGuid().ToString(); if (!Guid.TryParse(clientMessageId, out _)) throw new ApiProblem(400, "Invalid client message identifier.");
        var chat = await Member(actor, id); var sender = await profiles.Find(actor);
        if (replyToId != null) { Input.ObjectId(replyToId); if (!await db.Messages.Find(x => x.Id == replyToId && x.ChatId == id).AnyAsync()) throw new ApiProblem(400, "Reply message belongs to a different chat."); }
        await Gate.WaitAsync();
        try
        {
            var participants = await db.Users.Find(x => chat.Participants.Contains(x.Id)).ToListAsync();
            var result = await transactions.Run(async (session, ct) =>
            {
                var currentChat = await db.Chats.Find(session, x => x.Id == id && x.Participants.Contains(sender.Id)).FirstOrDefaultAsync(ct) ?? throw new ApiProblem(404, "Chat no longer exists.");
                var message = await db.Messages.Find(session, x => x.ChatId == id && x.SenderId == sender.Id && x.ClientMessageId == clientMessageId).FirstOrDefaultAsync(ct);
                if (message != null && (message.Content != content || message.ReplyToId != replyToId)) throw new ApiProblem(409, "Message identifier was already used for different content.");
                if (message == null) { message = new Message { ChatId = id, SenderId = sender.Id, Content = content, ClientMessageId = clientMessageId, ReplyToId = replyToId, SentAt = clock.GetUtcNow().UtcDateTime, ReadBy = [new MessageRead { UserId = actor, ReadAt = clock.GetUtcNow().UtcDateTime }] }; await db.Messages.InsertOneAsync(session, message, cancellationToken: ct); }
                await db.Chats.UpdateOneAsync(session, x => x.Id == id && x.LastActivity <= message.SentAt, Builders<Chat>.Update.Set(x => x.LastMessageId, message.Id).Set(x => x.LastActivity, message.SentAt), cancellationToken: ct);
                var notices = new List<Notification>();
                foreach (var participant in participants.Where(x => x.IdentityUserId != actor && !active.IsUserInChat(id, x.IdentityUserId)))
                    notices.Add(await notifications.PersistAsync(session, new Notification { TargetUserId = participant.IdentityUserId, SourceUserId = actor, Type = NotificationType.Message, Title = $"New message from {sender.UserName}", Message = content[..Math.Min(content.Length, 80)], Data = new() { ["chatId"] = id, ["messageId"] = message.Id }, ActionUrl = $"/chat/{id}", Key = $"message:{message.Id}:{participant.IdentityUserId}", CreatedAt = clock.GetUtcNow().UtcDateTime }, ct));
                return (message, notices);
            });
            var dto = MessageDto(result.message, sender);
            foreach (var participant in participants)
            {
                await hub.Clients.Group($"chat_user_{participant.IdentityUserId}").SendAsync("ReceiveMessage", dto);
                await friendHub.Clients.Group($"user_{participant.IdentityUserId}").SendAsync("NewMessage", dto);
            }
            foreach (var n in result.notices) await notifications.PublishAsync(n);
            return dto;
        }
        finally { Gate.Release(); }
    }
    public async Task Read(string actor, string messageId)
    {
        Input.ObjectId(messageId); var message = await db.Messages.Find(x => x.Id == messageId).FirstOrDefaultAsync() ?? throw new ApiProblem(404, "Message not found."); await Member(actor, message.ChatId);
        await db.Messages.UpdateOneAsync(x => x.Id == messageId && !x.ReadBy.Any(r => r.UserId == actor), Builders<Message>.Update.Push(x => x.ReadBy, new MessageRead { UserId = actor }));
        await hub.Clients.Group($"chat_{message.ChatId}").SendAsync("MessageRead", new { messageId, userId = actor, readAt = DateTime.UtcNow });
    }
    public async Task ReadAll(string actor, string chatId)
    {
        await Member(actor, chatId);
        await db.Messages.UpdateManyAsync(x => x.ChatId == chatId && !x.ReadBy.Any(r => r.UserId == actor), Builders<Message>.Update.Push(x => x.ReadBy, new MessageRead { UserId = actor }));
        await hub.Clients.Group($"chat_{chatId}").SendAsync("AllMessagesRead", new { chatId, userId = actor, readAt = DateTime.UtcNow });
    }
}
