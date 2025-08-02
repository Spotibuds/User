using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Services;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatsController : ControllerBase
{
    private readonly MongoDbContext _context;
    private readonly IRabbitMqService _rabbitMq;

    public ChatsController(MongoDbContext context, IRabbitMqService rabbitMq)
    {
        _context = context;
        _rabbitMq = rabbitMq;
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
            var currentUserId = User.Identity?.Name;
            if (string.IsNullOrEmpty(currentUserId))
            {
                return Unauthorized();
            }

            // For one-on-one chats, check if chat already exists
            if (!dto.IsGroup && dto.ParticipantIds.Count == 2)
            {
                var existingChat = await _context.Chats
                    .Find(c => !c.IsGroup && 
                              c.Participants.Contains(dto.ParticipantIds[0]) && 
                              c.Participants.Contains(dto.ParticipantIds[1]))
                    .FirstOrDefaultAsync();

                if (existingChat != null)
                {
                    return Ok(new
                    {
                        chatId = existingChat.Id,
                        isGroup = existingChat.IsGroup,
                        name = existingChat.Name,
                        participants = existingChat.Participants,
                        lastActivity = existingChat.LastActivity,
                        existed = true
                    });
                }
            }

            // Create new chat
            var chat = new Entities.Chat
            {
                Participants = dto.ParticipantIds,
                IsGroup = dto.IsGroup,
                Name = dto.Name,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            await _context.Chats.InsertOneAsync(chat);

            return Ok(new
            {
                chatId = chat.Id,
                isGroup = chat.IsGroup,
                name = chat.Name,
                participants = chat.Participants,
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
        var currentUserId = User.Identity?.Name;
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized();
        }

        // Check if requesting user has access to this chat
        var chat = await _context.Chats
            .Find(c => c.Id == id)
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound();
        }

        if (!chat.Participants.Contains(currentUserId))
        {
            return Forbid();
        }

        return Ok(new
        {
            chatId = chat.Id,
            isGroup = chat.IsGroup,
            name = chat.Name,
            participants = chat.Participants,
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

        try
        {
            var chats = await _context.Chats
                .Find(c => c.Participants.Contains(userId))
                .SortByDescending(c => c.LastActivity)
                .ToListAsync();

            var chatList = chats.Select(chat => new
            {
                chatId = chat.Id,
                isGroup = chat.IsGroup,
                name = chat.Name,
                participants = chat.Participants,
                lastActivity = chat.LastActivity,
                createdAt = chat.CreatedAt
            }).ToList();

            return Ok(chatList);
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

        var currentUserId = User.Identity?.Name;
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized();
        }

        // Check if requesting user has access to this chat
        var chat = await _context.Chats
            .Find(c => c.Id == chatId)
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound();
        }

        if (!chat.Participants.Contains(currentUserId))
        {
            return Forbid();
        }

        var messages = await _context.Messages
            .Find(m => m.ChatId == chatId)
            .SortByDescending(m => m.SentAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();

        var result = messages.Select(m => new
        {
            messageId = m.Id,
            chatId = m.ChatId,
            senderId = m.SenderId,
            content = m.Content,
            type = m.Type.ToString(),
            sentAt = m.SentAt,
            isEdited = m.IsEdited,
            editedAt = m.EditedAt,
            readBy = m.ReadBy,
            replyToId = m.ReplyToId
        });

        return Ok(result);
    }

    /// <summary>
    /// Send a message to a chat
    /// </summary>
    [HttpPost("{chatId}/messages")]
    public async Task<ActionResult<object>> SendMessage(string chatId, [FromBody] SendMessageDto dto)
    {
        var currentUserId = User.Identity?.Name;
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized();
        }

        // Check if requesting user has access to this chat
        var chat = await _context.Chats
            .Find(c => c.Id == chatId)
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound();
        }

        if (!chat.Participants.Contains(currentUserId))
        {
            return Forbid();
        }

        var message = new Entities.Message
        {
            ChatId = chatId,
            SenderId = currentUserId,
            Content = dto.Content,
            Type = Enum.Parse<Entities.MessageType>(dto.Type, true),
            SentAt = DateTime.UtcNow,
            ReadBy = new List<Entities.MessageRead>
            {
                new Entities.MessageRead { UserId = currentUserId, ReadAt = DateTime.UtcNow }
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
            SenderId = currentUserId,
            Content = dto.Content,
            SentAt = message.SentAt,
            Recipients = chat.Participants.Where(p => p != currentUserId).ToList()
        };

        await _rabbitMq.PublishMessageAsync("chat.message", notification);

        return Ok(new
        {
            messageId = message.Id,
            chatId = message.ChatId,
            senderId = message.SenderId,
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
        var currentUserId = User.Identity?.Name;
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized();
        }

        var message = await _context.Messages
            .Find(m => m.Id == messageId)
            .FirstOrDefaultAsync();

        if (message == null)
        {
            return NotFound();
        }

        // Check if user has access to this message (through chat participation)
        var chat = await _context.Chats
            .Find(c => c.Id == message.ChatId && c.Participants.Contains(currentUserId))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return Forbid();
        }

        return Ok(new
        {
            messageId = message.Id,
            chatId = message.ChatId,
            senderId = message.SenderId,
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
        var currentUserId = User.Identity?.Name;
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized();
        }

        var message = await _context.Messages
            .Find(m => m.Id == messageId)
            .FirstOrDefaultAsync();

        if (message == null)
        {
            return NotFound();
        }

        // Check if user has access to this message
        var chat = await _context.Chats
            .Find(c => c.Id == message.ChatId && c.Participants.Contains(currentUserId))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return Forbid();
        }

        // Check if already marked as read
        if (message.ReadBy.Any(r => r.UserId == currentUserId))
        {
            return Ok(new { message = "Already marked as read" });
        }

        // Add read receipt
        await _context.Messages.UpdateOneAsync(
            m => m.Id == messageId,
            Builders<Entities.Message>.Update.Push(m => m.ReadBy, new Entities.MessageRead
            {
                UserId = currentUserId,
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
        var currentUserId = User.Identity?.Name;
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Unauthorized();
        }

        var chat = await _context.Chats
            .Find(c => c.Id == chatId && c.Participants.Contains(currentUserId))
            .FirstOrDefaultAsync();

        if (chat == null)
        {
            return NotFound();
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