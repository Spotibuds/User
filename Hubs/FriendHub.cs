using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Models;
using System.Security.Claims;

namespace User.Hubs;

public class FriendHub : Hub
{
    private readonly MongoDbContext _context;
    private readonly ILogger<FriendHub> _logger;
    private static readonly Dictionary<string, string> _userConnections = new();

    public FriendHub(MongoDbContext context, ILogger<FriendHub> logger)
    {
        _context = context;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        try
        {
            var userId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning($"Unauthorized connection attempt: {Context.ConnectionId}");
                Context.Abort();
                return;
            }

            // Store user connection mapping
            _userConnections[userId] = Context.ConnectionId;
            
            // Add to user group
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
            
            // Add to online users group
            await Groups.AddToGroupAsync(Context.ConnectionId, "online_users");
            
            // Notify friends that user is online
            await NotifyFriendsOnlineStatus(userId, true);
            
            _logger.LogInformation($"User {userId} connected: {Context.ConnectionId}");
            
            await base.OnConnectedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnConnectedAsync");
            throw;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            var userId = GetUserIdFromContext();
            if (!string.IsNullOrEmpty(userId))
            {
                // Remove from connection mapping
                _userConnections.Remove(userId);
                
                // Remove from groups
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, "online_users");
                
                // Notify friends that user is offline
                await NotifyFriendsOnlineStatus(userId, false);
                
                _logger.LogInformation($"User {userId} disconnected: {Context.ConnectionId}");
            }
            
            await base.OnDisconnectedAsync(exception);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnDisconnectedAsync");
        }
    }

    // Friend Request Methods
    public async Task SendFriendRequest(string targetUserId)
    {
        try
        {
            var senderId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(senderId))
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            // Check if request already exists
            var existingRequest = await _context.Friends
                .Find(f => (f.UserId == senderId && f.FriendId == targetUserId) ||
                           (f.UserId == targetUserId && f.FriendId == senderId))
                .FirstOrDefaultAsync();

            if (existingRequest != null)
            {
                await Clients.Caller.SendAsync("Error", "Friend request already exists");
                return;
            }

            // Create friend request
            var friendRequest = new Friend
            {
                UserId = senderId,
                FriendId = targetUserId,
                Status = FriendStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            await _context.Friends.InsertOneAsync(friendRequest);

            // Get sender info for notification
            var sender = await _context.Users.Find(u => u.Id == senderId).FirstOrDefaultAsync();
            
            // Notify target user
            await Clients.Group($"user_{targetUserId}").SendAsync("FriendRequestReceived", new
            {
                RequestId = friendRequest.Id,
                SenderId = senderId,
                SenderName = sender?.UserName ?? "Unknown User",
                SenderAvatar = sender?.AvatarUrl,
                Timestamp = friendRequest.CreatedAt
            });

            await Clients.Caller.SendAsync("FriendRequestSent", new
            {
                RequestId = friendRequest.Id,
                TargetUserId = targetUserId,
                Timestamp = friendRequest.CreatedAt
            });

            _logger.LogInformation($"Friend request sent from {senderId} to {targetUserId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending friend request");
            await Clients.Caller.SendAsync("Error", "Failed to send friend request");
        }
    }

    public async Task AcceptFriendRequest(string requestId)
    {
        try
        {
            var userId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(userId))
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            var friendRequest = await _context.Friends.Find(f => f.Id == requestId && f.FriendId == userId).FirstOrDefaultAsync();
            if (friendRequest == null)
            {
                await Clients.Caller.SendAsync("Error", "Friend request not found");
                return;
            }

            // Update status to accepted
            var update = Builders<Friend>.Update.Set(f => f.Status, FriendStatus.Accepted);
            await _context.Friends.UpdateOneAsync(f => f.Id == requestId, update);

            // Get user info for notifications
            var user = await _context.Users.Find(u => u.Id == userId).FirstOrDefaultAsync();
            var friend = await _context.Users.Find(u => u.Id == friendRequest.UserId).FirstOrDefaultAsync();

            // Notify both users
            await Clients.Group($"user_{friendRequest.UserId}").SendAsync("FriendRequestAccepted", new
            {
                RequestId = requestId,
                AcceptedBy = userId,
                AcceptedByName = user?.UserName ?? "Unknown User",
                AcceptedByAvatar = user?.AvatarUrl,
                Timestamp = DateTime.UtcNow
            });

            await Clients.Caller.SendAsync("FriendRequestAccepted", new
            {
                RequestId = requestId,
                FriendId = friendRequest.UserId,
                FriendName = friend?.UserName ?? "Unknown User",
                FriendAvatar = friend?.AvatarUrl,
                Timestamp = DateTime.UtcNow
            });

            _logger.LogInformation($"Friend request {requestId} accepted by {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting friend request");
            await Clients.Caller.SendAsync("Error", "Failed to accept friend request");
        }
    }

    public async Task DeclineFriendRequest(string requestId)
    {
        try
        {
            var userId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(userId))
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            var friendRequest = await _context.Friends.Find(f => f.Id == requestId && f.FriendId == userId).FirstOrDefaultAsync();
            if (friendRequest == null)
            {
                await Clients.Caller.SendAsync("Error", "Friend request not found");
                return;
            }

            // Delete the friend request
            await _context.Friends.DeleteOneAsync(f => f.Id == requestId);

            // Notify sender
            await Clients.Group($"user_{friendRequest.UserId}").SendAsync("FriendRequestDeclined", new
            {
                RequestId = requestId,
                DeclinedBy = userId,
                Timestamp = DateTime.UtcNow
            });

            await Clients.Caller.SendAsync("FriendRequestDeclined", new
            {
                RequestId = requestId,
                Timestamp = DateTime.UtcNow
            });

            _logger.LogInformation($"Friend request {requestId} declined by {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error declining friend request");
            await Clients.Caller.SendAsync("Error", "Failed to decline friend request");
        }
    }

    public async Task RemoveFriend(string friendId)
    {
        try
        {
            var userId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(userId))
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            // Find and remove friend relationship
            var friendRelationship = await _context.Friends
                .Find(f => (f.UserId == userId && f.FriendId == friendId) ||
                           (f.UserId == friendId && f.FriendId == userId))
                .FirstOrDefaultAsync();

            if (friendRelationship == null)
            {
                await Clients.Caller.SendAsync("Error", "Friend relationship not found");
                return;
            }

            await _context.Friends.DeleteOneAsync(f => f.Id == friendRelationship.Id);

            // Notify both users
            await Clients.Group($"user_{friendId}").SendAsync("FriendRemoved", new
            {
                RemovedBy = userId,
                Timestamp = DateTime.UtcNow
            });

            await Clients.Caller.SendAsync("FriendRemoved", new
            {
                RemovedFriendId = friendId,
                Timestamp = DateTime.UtcNow
            });

            _logger.LogInformation($"Friend relationship removed between {userId} and {friendId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing friend");
            await Clients.Caller.SendAsync("Error", "Failed to remove friend");
        }
    }

    // Chat Methods
    public async Task SendMessage(string chatId, string message)
    {
        try
        {
            var senderId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(senderId))
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            // Validate chat access
            var chat = await _context.Chats.Find(c => c.Id == chatId).FirstOrDefaultAsync();
            if (chat == null || (!chat.Participants.Contains(senderId)))
            {
                await Clients.Caller.SendAsync("Error", "Chat not found or access denied");
                return;
            }

            // Create message
            var newMessage = new Message
            {
                ChatId = chatId,
                SenderId = senderId,
                Content = message,
                SentAt = DateTime.UtcNow
            };

            await _context.Messages.InsertOneAsync(newMessage);

            // Get sender info
            var sender = await _context.Users.Find(u => u.Id == senderId).FirstOrDefaultAsync();

            // Notify all chat participants
            foreach (var participantId in chat.Participants)
            {
                if (participantId != senderId) // Don't send to sender
                {
                    await Clients.Group($"user_{participantId}").SendAsync("MessageReceived", new
                    {
                        ChatId = chatId,
                        MessageId = newMessage.Id,
                        SenderId = senderId,
                        SenderName = sender?.UserName ?? "Unknown User",
                        SenderAvatar = sender?.AvatarUrl,
                        Content = message,
                        Timestamp = newMessage.SentAt
                    });
                }
            }

            // Confirm message sent to sender
            await Clients.Caller.SendAsync("MessageSent", new
            {
                ChatId = chatId,
                MessageId = newMessage.Id,
                Content = message,
                Timestamp = newMessage.SentAt
            });

            _logger.LogInformation($"Message sent in chat {chatId} by {senderId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message");
            await Clients.Caller.SendAsync("Error", "Failed to send message");
        }
    }

    public async Task MarkMessageAsRead(string messageId)
    {
        try
        {
            var userId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(userId))
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            var message = await _context.Messages.Find(m => m.Id == messageId).FirstOrDefaultAsync();
            if (message == null)
            {
                await Clients.Caller.SendAsync("Error", "Message not found");
                return;
            }

            // Check if user is participant in the chat
            var chat = await _context.Chats.Find(c => c.Id == message.ChatId).FirstOrDefaultAsync();
            if (chat == null || !chat.Participants.Contains(userId))
            {
                await Clients.Caller.SendAsync("Error", "Access denied");
                return;
            }

            // Mark as read
            var readBy = new MessageRead
            {
                UserId = userId,
                ReadAt = DateTime.UtcNow
            };
            var update = Builders<Message>.Update.Push(m => m.ReadBy, readBy);
            await _context.Messages.UpdateOneAsync(m => m.Id == messageId, update);

            // Notify message sender
            await Clients.Group($"user_{message.SenderId}").SendAsync("MessageRead", new
            {
                MessageId = messageId,
                ReadBy = userId,
                Timestamp = DateTime.UtcNow
            });

            _logger.LogInformation($"Message {messageId} marked as read by {userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking message as read");
            await Clients.Caller.SendAsync("Error", "Failed to mark message as read");
        }
    }

    public async Task CreateChat(string friendId)
    {
        try
        {
            var userId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(userId))
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            // Check if chat already exists
            var existingChat = await _context.Chats
                .Find(c => c.Participants.Contains(userId) && c.Participants.Contains(friendId))
                .FirstOrDefaultAsync();

            if (existingChat != null)
            {
                await Clients.Caller.SendAsync("ChatCreated", new
                {
                    ChatId = existingChat.Id,
                    Participants = existingChat.Participants,
                    CreatedAt = existingChat.CreatedAt
                });
                return;
            }

            // Create new chat
            var newChat = new Chat
            {
                Participants = new List<string> { userId, friendId },
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            await _context.Chats.InsertOneAsync(newChat);

            // Notify both users
            await Clients.Group($"user_{userId}").SendAsync("ChatCreated", new
            {
                ChatId = newChat.Id,
                Participants = newChat.Participants,
                CreatedAt = newChat.CreatedAt
            });

            await Clients.Group($"user_{friendId}").SendAsync("ChatCreated", new
            {
                ChatId = newChat.Id,
                Participants = newChat.Participants,
                CreatedAt = newChat.CreatedAt
            });

            _logger.LogInformation($"Chat created between {userId} and {friendId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating chat");
            await Clients.Caller.SendAsync("Error", "Failed to create chat");
        }
    }

    // Utility Methods
    private string? GetUserIdFromContext()
    {
        // In a real app, you'd get this from JWT token or session
        // For now, we'll use a query parameter or header
        var userId = Context.GetHttpContext()?.Request.Query["userId"].FirstOrDefault();
        if (string.IsNullOrEmpty(userId))
        {
            // Try to get from headers
            userId = Context.GetHttpContext()?.Request.Headers["X-User-Id"].FirstOrDefault();
        }
        return userId;
    }

    private async Task NotifyFriendsOnlineStatus(string userId, bool isOnline)
    {
        try
        {
            // Get user's friends
            var friends = await _context.Friends
                .Find(f => (f.UserId == userId || f.FriendId == userId) && f.Status == FriendStatus.Accepted)
                .ToListAsync();

            var friendIds = friends.Select(f => f.UserId == userId ? f.FriendId : f.UserId).ToList();

            // Get user info
            var user = await _context.Users.Find(u => u.Id == userId).FirstOrDefaultAsync();

            foreach (var friendId in friendIds)
            {
                await Clients.Group($"user_{friendId}").SendAsync("FriendStatusChanged", new
                {
                    FriendId = userId,
                    FriendName = user?.UserName ?? "Unknown User",
                    IsOnline = isOnline,
                    Timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying friends of online status");
        }
    }

    // Get online friends
    public async Task GetOnlineFriends()
    {
        try
        {
            var userId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(userId))
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            // Get user's friends
            var friends = await _context.Friends
                .Find(f => (f.UserId == userId || f.FriendId == userId) && f.Status == FriendStatus.Accepted)
                .ToListAsync();

            var friendIds = friends.Select(f => f.UserId == userId ? f.FriendId : f.UserId).ToList();
            var onlineFriends = friendIds.Where(id => _userConnections.ContainsKey(id)).ToList();

            await Clients.Caller.SendAsync("OnlineFriends", onlineFriends);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting online friends");
            await Clients.Caller.SendAsync("Error", "Failed to get online friends");
        }
    }
} 