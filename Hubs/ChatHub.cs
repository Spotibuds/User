using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Entities;

namespace User.Hubs;

public class ChatHub : Hub
{
    private readonly MongoDbContext _context;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(MongoDbContext context, ILogger<ChatHub> logger)
    {
        _context = context;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier?.ToString();
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
            _logger.LogInformation($"User {userId} connected to chat hub");
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier?.ToString();
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
            _logger.LogInformation($"User {userId} disconnected from chat hub");
        }
        await base.OnDisconnectedAsync(exception);
    }

    // In User/Hubs/ChatHub.cs
public async Task JoinChat(string chatId)
{
    var userId = Context.UserIdentifier?.ToString();
    if (string.IsNullOrEmpty(userId))
    {
        await Clients.Caller.SendAsync("Error", "User not authenticated");
        return;
    }

    // Get MongoDB user by IdentityUserId
    var user = await _context.Users
        .Find(u => u.IdentityUserId == userId)
        .FirstOrDefaultAsync();
        
    if (user == null)
    {
        await Clients.Caller.SendAsync("Error", "User not found");
        return;
    }

    // Now use the MongoDB _id to check chat participation
    var chat = await _context.Chats
        .Find(c => c.Id == chatId && c.Participants.Contains(user.Id))
        .FirstOrDefaultAsync();

    if (chat == null)
    {
        await Clients.Caller.SendAsync("Error", "Chat not found or access denied");
        return;
    }

    await Groups.AddToGroupAsync(Context.ConnectionId, $"chat_{chatId}");
    await Clients.Caller.SendAsync("JoinedChat", chatId);
    
    _logger.LogInformation($"User {userId} joined chat {chatId}");
}

    public async Task LeaveChat(string chatId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chat_{chatId}");
        await Clients.Caller.SendAsync("LeftChat", chatId);
        
        var userId = Context.UserIdentifier;
        _logger.LogInformation($"User {userId} left chat {chatId}");
    }

    public async Task SendMessage(string chatId, string content)
    {
        var userId = Context.UserIdentifier?.ToString();
        _logger.LogInformation($"SendMessage called: chatId={chatId}, content={content}, userId={userId}, connectionId={Context.ConnectionId}");
        if (string.IsNullOrEmpty(userId))
        {
            await Clients.Caller.SendAsync("Error", "User not authenticated");
            return;
        }

        // Convert IdentityUserId to MongoDB _id
        var user = await _context.Users
            .Find(u => u.IdentityUserId == userId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            await Clients.Caller.SendAsync("Error", "User not found in database");
            return;
        }

        // Verify user has access to this chat using MongoDB _id
        var chat = await _context.Chats
            .Find(c => c.Id == chatId && c.Participants.Contains(user.Id))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            await Clients.Caller.SendAsync("Error", "Chat not found or access denied");
            return;
        }

        // Create message with MongoDB _id
        var message = new Message
        {
            ChatId = chatId,
            SenderId = user.Id, // Use MongoDB _id
            Content = content,
            Type = MessageType.Text
        };

        await _context.Messages.InsertOneAsync(message);

        // Update chat's last activity and last message
        var updateDefinition = Builders<Chat>.Update
            .Set(c => c.LastMessageId, message.Id)
            .Set(c => c.LastActivity, DateTime.UtcNow)
            .Set(c => c.UpdatedAt, DateTime.UtcNow);

        await _context.Chats.UpdateOneAsync(c => c.Id == chatId, updateDefinition);

        // Update friendship's last activity time
        var participants = chat.Participants.Where(p => p != user.Id).ToList();
        foreach (var participantId in participants)
        {
            var friendshipUpdate = Builders<Friend>.Update
                .Set(f => f.CreatedAt, DateTime.UtcNow);

            await _context.Friends.UpdateManyAsync(
                f => (f.UserId == user.Id && f.FriendId == participantId) ||
                     (f.UserId == participantId && f.FriendId == user.Id),
                friendshipUpdate);
        }

        // Get sender info for response (convert back to IdentityUserId)
        var messageDto = new
        {
            Id = message.Id,
            ChatId = chatId,
            SenderId = userId, // Use IdentityUserId for frontend
            SenderUsername = user.UserName ?? "Unknown",
            Content = content,
            Type = message.Type.ToString(),
            CreatedAt = message.CreatedAt,
            IsEdited = false
        };

        // Send to all participants in the chat
        await Clients.Group($"chat_{chatId}").SendAsync("ReceiveMessage", messageDto);
        
        _logger.LogInformation($"Message sent in chat {chatId} by user {userId}");
    }

    public async Task MarkAsRead(string messageId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId))
        {
            await Clients.Caller.SendAsync("Error", "User not authenticated");
            return;
        }

        // Convert IdentityUserId to MongoDB _id
        var user = await _context.Users
            .Find(u => u.IdentityUserId == userId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            await Clients.Caller.SendAsync("Error", "User not found in database");
            return;
        }

        var message = await _context.Messages
            .Find(m => m.Id == messageId)
            .FirstOrDefaultAsync();

        if (message == null)
        {
            await Clients.Caller.SendAsync("Error", "Message not found");
            return;
        }

        // Check if user has access to this message (through chat participation) using MongoDB _id
        var chat = await _context.Chats
            .Find(c => c.Id == message.ChatId && c.Participants.Contains(user.Id))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            await Clients.Caller.SendAsync("Error", "Access denied");
            return;
        }

        // Add read receipt if not already read by this user (use MongoDB _id for storage)
        if (!message.ReadBy.Any(r => r.UserId == user.Id))
        {
            var readReceipt = new MessageRead
            {
                UserId = user.Id, // Use MongoDB _id
                ReadAt = DateTime.UtcNow
            };

            var updateDefinition = Builders<Message>.Update
                .Push(m => m.ReadBy, readReceipt);

            await _context.Messages.UpdateOneAsync(m => m.Id == messageId, updateDefinition);

            // Notify other participants that message was read (use IdentityUserId for frontend)
            await Clients.Group($"chat_{message.ChatId}").SendAsync("MessageRead", new
            {
                MessageId = messageId,
                UserId = userId, // Use IdentityUserId for frontend compatibility
                ReadAt = readReceipt.ReadAt
            });
        }
    }

    public async Task StartTyping(string chatId)
    {
        var userId = Context.UserIdentifier?.ToString();
        if (!string.IsNullOrEmpty(userId))
        {
            await Clients.OthersInGroup($"chat_{chatId}").SendAsync("UserStartedTyping", userId);
        }
    }

    public async Task StopTyping(string chatId)
    {
        var userId = Context.UserIdentifier?.ToString();
        if (!string.IsNullOrEmpty(userId))
        {
            await Clients.OthersInGroup($"chat_{chatId}").SendAsync("UserStoppedTyping", userId);
        }
    }
} 