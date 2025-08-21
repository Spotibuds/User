using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Services;
using System.Security.Claims;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatsController : ControllerBase
{
    private readonly MongoDbContext _context;
    private readonly IRabbitMqService? _rabbitMq;

    public ChatsController(MongoDbContext context, IRabbitMqService? rabbitMq)
    {
        _context = context;
        _rabbitMq = rabbitMq;
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
            Console.WriteLine("CreateOrGetChat: MongoDB not connected or Chats collection is null");
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
        Console.WriteLine($"GetUserChats: Starting request for userId: {userId}");

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

                chatList.Add(new
                {
                    chatId = chat.Id,
                    isGroup = chat.IsGroup,
                    name = chat.Name,
                    participants = participantIdentityIds,
                    lastActivity = chat.LastActivity,
                    createdAt = chat.CreatedAt
                });
            }

            stopwatch.Stop();
            Console.WriteLine($"GetUserChats: Completed successfully in {stopwatch.ElapsedMilliseconds}ms, returned {chatList.Count} chats");
            return Ok(chatList);
        }
        catch (MongoDB.Driver.MongoConnectionException ex)
        {
            stopwatch.Stop();
            Console.WriteLine($"GetUserChats: MongoDB connection error after {stopwatch.ElapsedMilliseconds}ms: {ex.Message}");
            return StatusCode(503, new { message = "Database temporarily unavailable", error = ex.Message });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Console.WriteLine($"GetUserChats: Unexpected error after {stopwatch.ElapsedMilliseconds}ms: {ex.Message}");
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
            Console.WriteLine("GetChatMessages: MongoDB not connected or collections are null");
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

        // Send notification to other participants
        var notification = new Services.ChatMessageNotification
        {
            ChatId = chatId,
            MessageId = message.Id,
            SenderId = user.Id, // Use MongoDB _id
            Content = dto.Content,
            SentAt = message.SentAt,
            Recipients = chat.Participants.Where(p => p != user.Id).ToList() // Use MongoDB _id
        };

        if (_rabbitMq != null)
        {
            await _rabbitMq.PublishMessageAsync("chat.message", notification);
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