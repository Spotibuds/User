using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Services;
using User.Hubs;
using System.Security.Claims;
using UserEntity = User.Models.User;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatsController : ControllerBase
{
    private readonly MongoDbContext _context;
    private readonly IRabbitMqService? _rabbitMq;
    private readonly INotificationService _notificationService;
    private readonly IHubContext<NotificationHub> _notificationHubContext;
    private readonly ILogger<ChatsController> _logger;
    private readonly IActiveChatTrackingService _activeChatTracking;

    public ChatsController(
        MongoDbContext context, 
        IRabbitMqService? rabbitMq,
        INotificationService notificationService,
        IHubContext<NotificationHub> notificationHubContext,
        ILogger<ChatsController> logger,
        IActiveChatTrackingService activeChatTracking)
    {
        _context = context;
        _rabbitMq = rabbitMq;
        _notificationService = notificationService;
        _notificationHubContext = notificationHubContext;
        _logger = logger;
        _activeChatTracking = activeChatTracking;
    }

    private string? GetUserIdFromClaims()
    {
        var user = User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var userIdClaim = user.FindFirst("sub") ?? 
                             user.FindFirst("userId") ?? 
                             user.FindFirst("nameid") ?? 
                             user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
            return userIdClaim?.Value;
        }
        return null;
    }

    /// <summary>
    /// Create or get existing chat between users
    /// </summary>
    [HttpPost("create-or-get")]
    public async Task<ActionResult<object>> CreateOrGetChat([FromBody] CreateChatDto dto)
    {
        if (!_context.IsConnected || _context.Chats == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var currentUserId = GetUserIdFromClaims();
            if (string.IsNullOrEmpty(currentUserId))
            {
                return Unauthorized("User not authenticated");
            }

            // Convert IdentityUserId to MongoDB _id for participants
            var participantMongoIds = new List<string>();
            foreach (var participantId in dto.ParticipantIds)
            {
                var user = await _context.Users
                    .Find(u => u.IdentityUserId == participantId)
                    .FirstOrDefaultAsync();
                
                if (user == null)
                {
                    return BadRequest($"User with IdentityUserId {participantId} not found");
                }
                
                participantMongoIds.Add(user.Id);
            }

            // For one-on-one chats, check if chat already exists
            if (!dto.IsGroup && participantMongoIds.Count == 2)
            {
                var existingChat = await _context.Chats
                    .Find(c => !c.IsGroup && 
                              c.Participants.Contains(participantMongoIds[0]) && 
                              c.Participants.Contains(participantMongoIds[1]))
                    .FirstOrDefaultAsync();

                if (existingChat != null)
                {
                    // Convert MongoDB _id back to IdentityUserId for participants
                    var existingParticipantIdentityIds = new List<string>();
                    foreach (var participantId in existingChat.Participants)
                    {
                        var participant = await _context.Users
                            .Find(u => u.Id == participantId)
                            .FirstOrDefaultAsync();
                        
                        if (participant != null)
                        {
                            existingParticipantIdentityIds.Add(participant.IdentityUserId);
                        }
                        else
                        {
                            // Fallback to original ID if user not found
                            existingParticipantIdentityIds.Add(participantId);
                        }
                    }

                    return Ok(new
                    {
                        chatId = existingChat.Id,
                        isGroup = existingChat.IsGroup,
                        name = existingChat.Name,
                        participants = existingParticipantIdentityIds,
                        lastActivity = existingChat.LastActivity,
                        existed = true
                    });
                }
            }

            // Create new chat
            var chat = new Entities.Chat
            {
                Participants = participantMongoIds,
                IsGroup = dto.IsGroup,
                Name = dto.Name,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            await _context.Chats.InsertOneAsync(chat);

            // Convert MongoDB _id back to IdentityUserId for participants in response
            var newParticipantIdentityIds = new List<string>();
            foreach (var participantId in chat.Participants)
            {
                var participant = await _context.Users
                    .Find(u => u.Id == participantId)
                    .FirstOrDefaultAsync();
                
                if (participant != null)
                {
                    newParticipantIdentityIds.Add(participant.IdentityUserId);
                }
                else
                {
                    // Fallback to original ID if user not found
                    newParticipantIdentityIds.Add(participantId);
                }
            }

            return Ok(new
            {
                chatId = chat.Id,
                isGroup = chat.IsGroup,
                name = chat.Name,
                participants = newParticipantIdentityIds,
                lastActivity = chat.LastActivity,
                existed = false
            });
        }
        catch (MongoDB.Driver.MongoConnectionException ex)
        {
            return StatusCode(503, new { message = "Database temporarily unavailable", error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Get a specific chat
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<object>> GetChat(string id)
    {
        var currentUserId = GetUserIdFromClaims();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized("User not authenticated");
        }

        // Convert IdentityUserId to MongoDB _id for current user
        var currentUser = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId)
            .FirstOrDefaultAsync();
            
        if (currentUser == null)
        {
            return Unauthorized("User not found in database");
        }

        // Check if requesting user has access to this chat
        var chat = await _context.Chats
            .Find(c => c.Id == id)
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound("Chat not found");
        }

        if (!chat.Participants.Contains(currentUser.Id))
        {
            return Forbid("Access denied to this chat");
        }

        // Convert MongoDB _id back to IdentityUserId for participants
        var participantIdentityIds = new List<string>();
        foreach (var participantId in chat.Participants)
        {
            var participant = await _context.Users
                .Find(u => u.Id == participantId)
                .FirstOrDefaultAsync();
            
            if (participant != null)
            {
                participantIdentityIds.Add(participant.IdentityUserId);
            }
            else
            {
                // Fallback to original ID if user not found
                participantIdentityIds.Add(participantId);
            }
        }

        return Ok(new
        {
            chatId = chat.Id,
            isGroup = chat.IsGroup,
            name = chat.Name,
            participants = participantIdentityIds,
            lastActivity = chat.LastActivity,
            lastMessageId = chat.LastMessageId
        });
    }

    /// <summary>
    /// Get all chats for a user
    /// </summary>
    [HttpGet("user/{userId}")]
    public async Task<ActionResult<IEnumerable<object>>> GetUserChats(string userId)
    {
        if (!_context.IsConnected || _context.Chats == null)
        {
            Console.WriteLine("GetUserChats: MongoDB not connected or Chats collection is null");
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Convert IdentityUserId to MongoDB _id
            var user = await _context.Users
                .Find(u => u.IdentityUserId == userId)
                .FirstOrDefaultAsync();
                
            if (user == null)
            {
                return NotFound("User not found");
            }

            var chats = await _context.Chats
                .Find(c => c.Participants.Contains(user.Id))
                .SortByDescending(c => c.LastActivity)
                .ToListAsync();

            if (chats.Count == 0)
            {
                return Ok(new List<object>());
            }

            // Get all unique participant IDs from all chats
            var allParticipantIds = chats
                .SelectMany(c => c.Participants)
                .Distinct()
                .ToList();

            // Batch query to get all participants at once
            var allParticipants = await _context.Users
                .Find(u => allParticipantIds.Contains(u.Id))
                .ToListAsync();

            // Create a lookup dictionary for fast access
            var participantLookup = allParticipants.ToDictionary(u => u.Id, u => u.IdentityUserId);

            var chatList = new List<object>();
            foreach (var chat in chats)
            {
                // Convert MongoDB _id back to IdentityUserId for participants using lookup
                var participantIdentityIds = new List<string>();
                foreach (var participantId in chat.Participants)
                {
                    if (participantLookup.TryGetValue(participantId, out var identityUserId))
                    {
                        participantIdentityIds.Add(identityUserId);
                    }
                    else
                    {
                        // Fallback to original ID if user not found
                        participantIdentityIds.Add(participantId);
                    }
                }

                // Get the last message content if it exists
                string? lastMessageContent = null;
                string? lastMessageSenderId = null;

                if (!string.IsNullOrEmpty(chat.LastMessageId))
                {
                    var lastMessage = await _context.Messages
                        .Find(m => m.Id == chat.LastMessageId)
                        .FirstOrDefaultAsync();

                    if (lastMessage != null)
                    {
                        lastMessageContent = lastMessage.Content;

                        // Convert sender MongoDB _id to IdentityUserId
                        var sender = await _context.Users
                            .Find(u => u.Id == lastMessage.SenderId)
                            .FirstOrDefaultAsync();
                        lastMessageSenderId = sender?.IdentityUserId ?? lastMessage.SenderId;
                    }
                    else
                    {
                        // LastMessageId exists but message not found - try to find the most recent message
                        var recentMessage = await _context.Messages
                            .Find(m => m.ChatId == chat.Id)
                            .SortByDescending(m => m.SentAt)
                            .FirstOrDefaultAsync();

                        if (recentMessage != null)
                        {
                            lastMessageContent = recentMessage.Content;
                            var sender = await _context.Users
                                .Find(u => u.Id == recentMessage.SenderId)
                                .FirstOrDefaultAsync();
                            lastMessageSenderId = sender?.IdentityUserId ?? recentMessage.SenderId;

                            // Update the chat's LastMessageId to fix it for future calls
                            await _context.Chats.UpdateOneAsync(
                                c => c.Id == chat.Id,
                                Builders<Entities.Chat>.Update
                                    .Set(c => c.LastMessageId, recentMessage.Id)
                                    .Set(c => c.LastActivity, recentMessage.SentAt));
                        }
                    }
                }
                else
                {
                    // No LastMessageId set - try to find the most recent message and set it
                    var recentMessage = await _context.Messages
                        .Find(m => m.ChatId == chat.Id)
                        .SortByDescending(m => m.SentAt)
                        .FirstOrDefaultAsync();

                    if (recentMessage != null)
                    {
                        lastMessageContent = recentMessage.Content;
                        var sender = await _context.Users
                            .Find(u => u.Id == recentMessage.SenderId)
                            .FirstOrDefaultAsync();
                        lastMessageSenderId = sender?.IdentityUserId ?? recentMessage.SenderId;

                        // Update the chat's LastMessageId to fix it for future calls
                        await _context.Chats.UpdateOneAsync(
                            c => c.Id == chat.Id,
                            Builders<Entities.Chat>.Update
                                .Set(c => c.LastMessageId, recentMessage.Id)
                                .Set(c => c.LastActivity, recentMessage.SentAt));
                    }
                }

                chatList.Add(new
                {
                    chatId = chat.Id,
                    isGroup = chat.IsGroup,
                    name = chat.Name,
                    participants = participantIdentityIds,
                    lastActivity = chat.LastActivity,
                    lastMessageId = chat.LastMessageId,
                    lastMessageContent = lastMessageContent,
                    lastMessageSenderId = lastMessageSenderId,
                    createdAt = chat.CreatedAt
                });
            }

            stopwatch.Stop();
            return Ok(chatList);
        }
        catch (MongoDB.Driver.MongoConnectionException ex)
        {
            stopwatch.Stop();
            return StatusCode(503, new { message = "Database temporarily unavailable", error = ex.Message });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return StatusCode(500, new { message = "Internal server error", error = ex.Message });
        }
    }

    /// <summary>
    /// Get messages for a chat
    /// </summary>
    [HttpGet("{chatId}/messages")]
    public async Task<ActionResult<IEnumerable<object>>> GetChatMessages(string chatId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (!_context.IsConnected || _context.Chats == null || _context.Messages == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        var currentUserId = GetUserIdFromClaims();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized("User not authenticated");
        }

        // Convert IdentityUserId to MongoDB _id
        var user = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            return Unauthorized("User not found in database");
        }

        // Check if requesting user has access to this chat
        var chat = await _context.Chats
            .Find(c => c.Id == chatId)
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound("Chat not found");
        }

        if (!chat.Participants.Contains(user.Id))
        {
            return Forbid("Access denied to this chat");
        }

        var messages = await _context.Messages
            .Find(m => m.ChatId == chatId)
            .SortByDescending(m => m.SentAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();

        var result = new List<object>();
        foreach (var m in messages)
        {
            // Convert sender MongoDB _id back to IdentityUserId
            var sender = await _context.Users
                .Find(u => u.Id == m.SenderId)
                .FirstOrDefaultAsync();

            result.Add(new
            {
                messageId = m.Id,
                chatId = m.ChatId,
                senderId = sender?.IdentityUserId ?? m.SenderId,
                senderName = sender?.UserName ?? "Unknown User",
                content = m.Content,
                type = m.Type.ToString(),
                sentAt = m.SentAt,
                isEdited = m.IsEdited,
                editedAt = m.EditedAt,
                readBy = m.ReadBy,
                replyToId = m.ReplyToId
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Send a message to a chat
    /// </summary>
    [HttpPost("{chatId}/messages")]
    public async Task<ActionResult<object>> SendMessage(string chatId, [FromBody] SendMessageDto dto)
    {
        var currentUserId = GetUserIdFromClaims();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized("User not authenticated");
        }

        // Convert IdentityUserId to MongoDB _id
        var user = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            return Unauthorized("User not found in database");
        }

        // Check if requesting user has access to this chat
        var chat = await _context.Chats
            .Find(c => c.Id == chatId)
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound("Chat not found");
        }

        if (!chat.Participants.Contains(user.Id))
        {
            return Forbid("Access denied to this chat");
        }

        var message = new Entities.Message
        {
            ChatId = chatId,
            SenderId = user.Id, // Use MongoDB _id
            Content = dto.Content,
            Type = Enum.Parse<Entities.MessageType>(dto.Type, true),
            SentAt = DateTime.UtcNow,
            ReadBy = new List<Entities.MessageRead>
            {
                new Entities.MessageRead { UserId = user.Id, ReadAt = DateTime.UtcNow } // Use MongoDB _id
            },
            ReplyToId = dto.ReplyToId
        };

        await _context.Messages.InsertOneAsync(message);

        // Update chat's last message and activity
        await _context.Chats.UpdateOneAsync(
            c => c.Id == chatId,
            Builders<Entities.Chat>.Update
                .Set(c => c.LastMessageId, message.Id)
                .Set(c => c.LastActivity, DateTime.UtcNow));

        // Send real-time notifications directly to other participants
        await SendChatMessageNotifications(chat, message, user, dto.Content);

        // Convert sender MongoDB _id back to IdentityUserId
        var sender = await _context.Users
            .Find(u => u.Id == message.SenderId)
            .FirstOrDefaultAsync();

        return Ok(new
        {
            messageId = message.Id,
            chatId = message.ChatId,
            senderId = sender?.IdentityUserId ?? message.SenderId,
            content = message.Content,
            type = message.Type.ToString(),
            sentAt = message.SentAt
        });
    }

    /// <summary>
    /// Get a specific message
    /// </summary>
    [HttpGet("messages/{messageId}")]
    public async Task<ActionResult<object>> GetMessage(string messageId)
    {
        var currentUserId = GetUserIdFromClaims();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized("User not authenticated");
        }

        var message = await _context.Messages
            .Find(m => m.Id == messageId)
            .FirstOrDefaultAsync();

        if (message == null)
        {
            return NotFound();
        }

        // Convert IdentityUserId to MongoDB _id for current user
        var user = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            return Unauthorized("User not found in database");
        }

        // Check if user has access to this message (through chat participation)
        var chat = await _context.Chats
            .Find(c => c.Id == message.ChatId && c.Participants.Contains(user.Id))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return Forbid();
        }

        // Convert sender MongoDB _id back to IdentityUserId
        var sender = await _context.Users
            .Find(u => u.Id == message.SenderId)
            .FirstOrDefaultAsync();

        return Ok(new
        {
            messageId = message.Id,
            chatId = message.ChatId,
            senderId = sender?.IdentityUserId ?? message.SenderId,
            content = message.Content,
            type = message.Type.ToString(),
            sentAt = message.SentAt,
            isEdited = message.IsEdited,
            editedAt = message.EditedAt,
            readBy = message.ReadBy,
            replyToId = message.ReplyToId
        });
    }

    /// <summary>
    /// Mark a message as read
    /// </summary>
    [HttpPost("messages/{messageId}/read")]
    public async Task<IActionResult> MarkMessageAsRead(string messageId)
    {
        var currentUserId = GetUserIdFromClaims();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized("User not authenticated");
        }

        // Convert IdentityUserId to MongoDB _id
        var user = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            return Unauthorized("User not found in database");
        }

        var message = await _context.Messages
            .Find(m => m.Id == messageId)
            .FirstOrDefaultAsync();

        if (message == null)
        {
            return NotFound("Message not found");
        }

        // Check if user has access to this message
        var chat = await _context.Chats
            .Find(c => c.Id == message.ChatId && c.Participants.Contains(user.Id))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return Forbid("Access denied to this message");
        }

        // Check if already marked as read
        if (message.ReadBy.Any(r => r.UserId == user.Id))
        {
            return Ok(new { message = "Already marked as read" });
        }

        // Add read receipt
        await _context.Messages.UpdateOneAsync(
            m => m.Id == messageId,
            Builders<Entities.Message>.Update.Push(m => m.ReadBy, new Entities.MessageRead
            {
                UserId = user.Id, // Use MongoDB _id
                ReadAt = DateTime.UtcNow
            }));

        return Ok(new { message = "Message marked as read" });
    }

    /// <summary>
    /// Delete a chat
    /// </summary>
    [HttpDelete("{chatId}")]
    public async Task<IActionResult> DeleteChat(string chatId)
    {
        var currentUserId = GetUserIdFromClaims();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized("User not authenticated");
        }

        // Convert IdentityUserId to MongoDB _id
        var user = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            return Unauthorized("User not found in database");
        }

        var chat = await _context.Chats
            .Find(c => c.Id == chatId && c.Participants.Contains(user.Id))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound("Chat not found or access denied");
        }

        // Delete all messages in the chat
        await _context.Messages.DeleteManyAsync(m => m.ChatId == chatId);

        // Delete the chat
        await _context.Chats.DeleteOneAsync(c => c.Id == chatId);

        return Ok(new { message = "Chat deleted successfully" });
    }

    /// <summary>
    /// Get unread message counts for all user chats
    /// </summary>
    [HttpGet("unread-counts")]
    public async Task<ActionResult<Dictionary<string, int>>> GetUnreadMessageCounts()
    {
        var currentUserId = GetUserIdFromClaims();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized("User not authenticated");
        }

        // Convert IdentityUserId to MongoDB _id
        var user = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            return Unauthorized("User not found in database");
        }

        // Get all chats for the user
        var userChats = await _context.Chats
            .Find(c => c.Participants.Contains(user.Id))
            .ToListAsync();

        var unreadCounts = new Dictionary<string, int>();

        foreach (var chat in userChats)
        {
            // Count messages in this chat that haven't been read by the current user
            // Exclude messages sent by the current user
            var unreadCount = await _context.Messages
                .Find(m => m.ChatId == chat.Id &&
                          m.SenderId != user.Id &&  // Exclude own messages
                          !m.ReadBy.Any(r => r.UserId == user.Id))
                .CountDocumentsAsync();


            unreadCounts[chat.Id] = (int)unreadCount;
        }

        return Ok(unreadCounts);
    }

    /// <summary>
    /// Get unread message count for a specific chat
    /// </summary>
    [HttpGet("{chatId}/unread-count")]
    public async Task<ActionResult<int>> GetChatUnreadCount(string chatId)
    {
        var currentUserId = GetUserIdFromClaims();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized("User not authenticated");
        }

        // Convert IdentityUserId to MongoDB _id
        var user = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            return Unauthorized("User not found in database");
        }

        // Verify user has access to this chat
        var chat = await _context.Chats
            .Find(c => c.Id == chatId && c.Participants.Contains(user.Id))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound("Chat not found or access denied");
        }

        // Count unread messages in this chat
        // Exclude messages sent by the current user
        var unreadCount = await _context.Messages
            .Find(m => m.ChatId == chatId && 
                      m.SenderId != user.Id &&  // Exclude own messages
                      !m.ReadBy.Any(r => r.UserId == user.Id))
            .CountDocumentsAsync();

        return Ok((int)unreadCount);
    }

    /// <summary>
    /// Mark all messages in a chat as read for the current user
    /// </summary>
    [HttpPost("{chatId}/mark-all-read")]
    public async Task<ActionResult> MarkAllMessagesAsRead(string chatId)
    {
        var currentUserId = GetUserIdFromClaims();
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized("User not authenticated");
        }

        // Convert IdentityUserId to MongoDB _id
        var user = await _context.Users
            .Find(u => u.IdentityUserId == currentUserId)
            .FirstOrDefaultAsync();
            
        if (user == null)
        {
            return Unauthorized("User not found in database");
        }

        // Verify user has access to this chat
        var chat = await _context.Chats
            .Find(c => c.Id == chatId && c.Participants.Contains(user.Id))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound("Chat not found or access denied");
        }

        // Get all unread messages in this chat
        var unreadMessages = await _context.Messages
            .Find(m => m.ChatId == chatId && 
                      !m.ReadBy.Any(r => r.UserId == user.Id))
            .ToListAsync();

        // Mark each message as read
        foreach (var message in unreadMessages)
        {
            message.ReadBy.Add(new Entities.MessageRead 
            { 
                UserId = user.Id, 
                ReadAt = DateTime.UtcNow 
            });

            await _context.Messages.ReplaceOneAsync(m => m.Id == message.Id, message);
        }

        return Ok(new { message = $"Marked {unreadMessages.Count} messages as read" });
    }

    /// <summary>
    /// Send real-time notifications for new chat messages directly via SignalR
    /// </summary>
    private async Task SendChatMessageNotifications(Entities.Chat chat, Entities.Message message, UserEntity sender, string content)
    {
        try
        {
            // Get recipient users (exclude sender)
            var recipientIds = chat.Participants.Where(p => p != sender.Id).ToList();
            var recipientUsers = await _context.Users
                .Find(u => recipientIds.Contains(u.Id))
                .ToListAsync();

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

                // Send real-time notification via SignalR (same pattern as friend notifications)
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

                // Update unread count
                var unreadCount = await _notificationService.GetUnreadCountAsync(recipient.IdentityUserId);
                await _notificationHubContext.Clients.Group($"notifications_{recipient.IdentityUserId}")
                    .SendAsync("UnreadCountUpdate", unreadCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send chat message notifications");
        }
    }
}

// DTOs
public class CreateChatDto
{
    public List<string> ParticipantIds { get; set; } = new();
    public bool IsGroup { get; set; } = false;
    public string Name { get; set; } = string.Empty;
}

public class SendMessageDto
{
    public string Content { get; set; } = string.Empty;
    public string Type { get; set; } = "Text";
    public string? ReplyToId { get; set; }
} 