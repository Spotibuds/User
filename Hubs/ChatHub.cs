using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Services;

namespace User.Hubs;

public class ChatHub : Hub
{
    private readonly MongoDbContext _context;
    private readonly ILogger<ChatHub> _logger;
    private readonly INotificationService _notificationService;
    private readonly IHubContext<NotificationHub> _notificationHubContext;
    private readonly IActiveChatTrackingService _activeChatTracking;

    public ChatHub(
        MongoDbContext context, 
        ILogger<ChatHub> logger,
        INotificationService notificationService,
        IHubContext<NotificationHub> notificationHubContext,
        IActiveChatTrackingService activeChatTracking)
    {
        _context = context;
        _logger = logger;
        _notificationService = notificationService;
        _notificationHubContext = notificationHubContext;
        _activeChatTracking = activeChatTracking;
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
            
            // Remove user from all active chats
            _activeChatTracking.RemoveUserFromAllChats(userId);
            
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
    
    // Track that this user is currently viewing the chat
    _activeChatTracking.AddUserToChat(chatId, userId);
    
    await Clients.Caller.SendAsync("JoinedChat", chatId);
    
    _logger.LogInformation($"User {userId} joined chat {chatId}");
}

    public async Task LeaveChat(string chatId)
    {
        var userId = Context.UserIdentifier;
        
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chat_{chatId}");
        
        // Remove user from active chat tracking
        if (!string.IsNullOrEmpty(userId))
        {
            _activeChatTracking.RemoveUserFromChat(chatId, userId);
        }
        
        await Clients.Caller.SendAsync("LeftChat", chatId);
        
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
        
        // Send notifications to other participants
        await SendChatMessageNotifications(chat, message, user, content);
        
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

    /// <summary>
    /// Send real-time notifications for new chat messages directly via SignalR
    /// Only sends notifications to users who are NOT currently viewing the chat
    /// </summary>
    private async Task SendChatMessageNotifications(Entities.Chat chat, Entities.Message message, User.Models.User sender, string content)
    {
        try
        {
            // Get recipient users (exclude sender)
            var recipientIds = chat.Participants.Where(p => p != sender.Id).ToList();
            Console.WriteLine($"📢 DEBUG: chat.Participants = [{string.Join(", ", chat.Participants)}]");
            Console.WriteLine($"📢 DEBUG: sender.Id = {sender.Id}");
            Console.WriteLine($"📢 DEBUG: recipientIds = [{string.Join(", ", recipientIds)}]");

            var recipientUsers = await _context.Users
                .Find(u => recipientIds.Contains(u.Id))
                .ToListAsync();

            Console.WriteLine($"📢 DEBUG: Found {recipientUsers.Count} recipient users");
            foreach (var user in recipientUsers)
            {
                Console.WriteLine($"📢 DEBUG: Recipient user - Id: {user.Id}, IdentityUserId: {user.IdentityUserId}, UserName: {user.UserName}");
            }

            foreach (var recipient in recipientUsers)
            {
                if (recipient.IdentityUserId == null) continue;

                // Skip notification if user is currently viewing the chat
                if (_activeChatTracking.IsUserInChat(chat.Id, recipient.IdentityUserId))
                {
                    _logger.LogInformation($"Skipping notification for user {recipient.IdentityUserId} - currently viewing chat {chat.Id}");
                    continue;
                }

                // Create persistent notification
                Console.WriteLine($"📢 DEBUG: recipient.Id = {recipient.Id}");
                Console.WriteLine($"📢 DEBUG: recipient.IdentityUserId = {recipient.IdentityUserId}");
                Console.WriteLine($"📢 DEBUG: recipient.UserName = {recipient.UserName}");
                Console.WriteLine($"📢 DEBUG: sender.Id = {sender.Id}");
                Console.WriteLine($"📢 DEBUG: sender.IdentityUserId = {sender.IdentityUserId}");
                Console.WriteLine($"📢 Creating notification for {recipient.IdentityUserId} about message from {sender.UserName ?? "Unknown User"}");

                if (string.IsNullOrEmpty(recipient.IdentityUserId))
                {
                    Console.WriteLine($"❌ ERROR: recipient.IdentityUserId is null or empty! Cannot create notification.");
                    continue;
                }

                await _notificationService.CreateNotificationAsync(
                    targetUserId: recipient.IdentityUserId,
                    type: Entities.NotificationType.Message,
                    title: $"New message from {sender.UserName ?? "Unknown User"}",
                    message: content.Length > 50 ? $"{content.Substring(0, 50)}..." : content,
                    sourceUserId: sender.IdentityUserId,
                    data: new Dictionary<string, object>
                    {
                        { "chatId", chat.Id },
                        { "messageId", message.Id },
                        { "senderUsername", sender.UserName ?? "Unknown User" },
                        { "senderAvatar", sender.AvatarUrl ?? "" }
                    },
                    actionUrl: $"/chat/{chat.Id}"
                );
                Console.WriteLine($"✅ Notification created for {recipient.IdentityUserId}");

                // Send real-time notification via SignalR (same pattern as friend notifications)
                Console.WriteLine($"📡 Sending SignalR notification to group notifications_{recipient.IdentityUserId}");
                await _notificationHubContext.Clients.Group($"notifications_{recipient.IdentityUserId}")
                    .SendAsync("NewNotification", new
                    {
                        Type = "Message",
                        Title = $"New message from {sender.UserName ?? "Unknown User"}",
                        Message = content.Length > 50 ? $"{content.Substring(0, 50)}..." : content,
                        SourceUserId = sender.IdentityUserId,
                        SourceUserName = sender.UserName ?? "Unknown User",
                        SourceUserAvatar = sender.AvatarUrl ?? "",
                        Data = new Dictionary<string, object>
                        {
                            { "chatId", chat.Id },
                            { "messageId", message.Id },
                            { "senderUsername", sender.UserName ?? "Unknown User" },
                            { "senderAvatar", sender.AvatarUrl ?? "" }
                        },
                        ActionUrl = $"/chat/{chat.Id}",
                        Timestamp = message.SentAt.ToString("O")
                    });
                Console.WriteLine($"📡 SignalR notification sent successfully");

                // Update unread count for this chat
                var chatUnreadCount = await _context.Messages
                    .Find(m => m.ChatId == chat.Id &&
                              m.SenderId != recipient.Id &&
                              !m.ReadBy.Any(r => r.UserId == recipient.Id))
                    .CountDocumentsAsync();

                await _notificationHubContext.Clients.Group($"notifications_{recipient.IdentityUserId}")
                    .SendAsync("ChatUnreadCountUpdate", new
                    {
                        chatId = chat.Id,
                        unreadCount = (int)chatUnreadCount
                    });

                // Update general unread count
                var unreadCount = await _notificationService.GetUnreadCountAsync(recipient.IdentityUserId);
                await _notificationHubContext.Clients.Group($"notifications_{recipient.IdentityUserId}")
                    .SendAsync("UnreadCountUpdate", unreadCount);

                _logger.LogInformation($"Sent notification to user {recipient.IdentityUserId} for message in chat {chat.Id}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send chat message notifications");
        }
    }
} 