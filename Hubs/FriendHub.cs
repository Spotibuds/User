using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using System.Net.Http;

namespace User.Hubs;

[Authorize]
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
            _logger.LogInformation($"SignalR connection attempt from: {Context.ConnectionId}");
            
            // Debug: Log all query parameters
            var httpContext = Context.GetHttpContext();
            var queryParams = httpContext?.Request.Query;
            _logger.LogInformation($"Query parameters: {string.Join(", ", queryParams?.Select(q => $"{q.Key}={q.Value}") ?? Array.Empty<string>())}");
            
            // Debug: Log authorization header
            var authHeader = httpContext?.Request.Headers["Authorization"].FirstOrDefault();
            _logger.LogInformation($"Authorization header present: {!string.IsNullOrEmpty(authHeader)}");
            
            var userId = GetUserIdFromContext();
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning($"Unauthorized connection attempt: {Context.ConnectionId}");
                Context.Abort();
                return;
            }

            // Check if user exists in MongoDB, if not, try to create them
            var user = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            if (user == null)
            {
                _logger.LogWarning($"User {userId} not found in MongoDB, attempting to create from Identity service");
                
                // Try to get user info from Identity service and create in MongoDB
                try
                {
                    user = await CreateUserFromIdentityService(userId);
                    if (user == null)
                    {
                        _logger.LogError($"Failed to create user {userId} in MongoDB from Identity service");
                        Context.Abort();
                        return;
                    }
                    _logger.LogInformation($"Successfully created user {userId} in MongoDB from Identity service");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error creating user {userId} in MongoDB from Identity service");
                    Context.Abort();
                    return;
                }
            }

            // Store user connection mapping
            _userConnections[userId] = Context.ConnectionId;
            
            // Add to user group
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
            
            // Add to online users group
            await Groups.AddToGroupAsync(Context.ConnectionId, "online_users");
            
            // Notify friends that user is online
            await NotifyFriendsOnlineStatus(userId, true);
            
            _logger.LogInformation($"User {userId} connected successfully: {Context.ConnectionId}");
            
            await base.OnConnectedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnConnectedAsync for connection {ConnectionId}", Context.ConnectionId);
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

            // Convert IdentityUserIds to MongoDB _ids for database queries
            var sender = await _context.Users.Find(u => u.IdentityUserId == senderId).FirstOrDefaultAsync();
            var target = await _context.Users.Find(u => u.IdentityUserId == targetUserId).FirstOrDefaultAsync();
            
            if (sender == null || target == null)
            {
                await Clients.Caller.SendAsync("Error", "User not found");
                return;
            }

            var senderMongoId = sender.Id;
            var targetMongoId = target.Id;

            // Check if request already exists
            var existingRequest = await _context.Friends
                .Find(f => (f.UserId == senderMongoId && f.FriendId == targetMongoId) ||
                           (f.UserId == targetMongoId && f.FriendId == senderMongoId))
                .FirstOrDefaultAsync();

            if (existingRequest != null)
            {
                await Clients.Caller.SendAsync("Error", "Friend request already exists");
                return;
            }

            // Create friend request
            var friendRequest = new Friend
            {
                UserId = senderMongoId,
                FriendId = targetMongoId,
                Status = FriendStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            await _context.Friends.InsertOneAsync(friendRequest);
            
            // Notify target user
            await Clients.Group($"user_{targetUserId}").SendAsync("FriendRequestReceived", new
            {
                RequestId = friendRequest.Id,
                SenderId = senderId, // Use IdentityUserId for frontend
                SenderName = sender.UserName ?? "Unknown User",
                SenderAvatar = sender.AvatarUrl,
                Timestamp = friendRequest.CreatedAt.ToString("O") // ISO 8601 format
            });

            await Clients.Caller.SendAsync("FriendRequestSent", new
            {
                RequestId = friendRequest.Id,
                TargetUserId = targetUserId, // Use IdentityUserId for frontend
                Timestamp = friendRequest.CreatedAt.ToString("O") // ISO 8601 format
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

            // Convert IdentityUserId to MongoDB _id for database queries
            var user = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            if (user == null)
            {
                await Clients.Caller.SendAsync("Error", "User not found");
                return;
            }

            var userMongoId = user.Id;

            var friendRequest = await _context.Friends.Find(f => f.Id == requestId && f.FriendId == userMongoId).FirstOrDefaultAsync();
            if (friendRequest == null)
            {
                await Clients.Caller.SendAsync("Error", "Friend request not found");
                return;
            }

            // Update status to accepted
            var update = Builders<Friend>.Update.Set(f => f.Status, FriendStatus.Accepted);
            await _context.Friends.UpdateOneAsync(f => f.Id == requestId, update);

            // Get friend info for notifications
            var friend = await _context.Users.Find(u => u.Id == friendRequest.UserId).FirstOrDefaultAsync();
            var friendIdentityId = friend?.IdentityUserId;

            // Notify both users
            if (!string.IsNullOrEmpty(friendIdentityId))
            {
                await Clients.Group($"user_{friendIdentityId}").SendAsync("FriendRequestAccepted", new
                {
                    RequestId = requestId,
                    AcceptedBy = userId, // Use IdentityUserId for frontend
                    AcceptedByName = user.UserName ?? "Unknown User",
                    AcceptedByAvatar = user.AvatarUrl,
                    Timestamp = DateTime.UtcNow.ToString("O") // ISO 8601 format
                });
            }

            await Clients.Caller.SendAsync("FriendRequestAccepted", new
            {
                RequestId = requestId,
                FriendId = friendIdentityId ?? friendRequest.UserId, // Use IdentityUserId for frontend
                FriendName = friend?.UserName ?? "Unknown User",
                FriendAvatar = friend?.AvatarUrl,
                Timestamp = DateTime.UtcNow.ToString("O") // ISO 8601 format
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
        _logger.LogInformation($"⚠️  FriendHub.DeclineFriendRequest DISABLED to prevent duplicate notifications. RequestId: {requestId}");
        // TEMPORARILY DISABLED: This method was causing duplicate notifications with HTTP API
        await Clients.Caller.SendAsync("Error", "Please use the web interface to decline friend requests");
        return;
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

            // Convert IdentityUserIds to MongoDB _ids for database queries
            var user = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            var friend = await _context.Users.Find(u => u.IdentityUserId == friendId).FirstOrDefaultAsync();
            
            if (user == null || friend == null)
            {
                await Clients.Caller.SendAsync("Error", "User not found");
                return;
            }

            var userMongoId = user.Id;
            var friendMongoId = friend.Id;

            // Find and remove friend relationship
            var friendRelationship = await _context.Friends
                .Find(f => (f.UserId == userMongoId && f.FriendId == friendMongoId) ||
                           (f.UserId == friendMongoId && f.FriendId == userMongoId))
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
                RemovedBy = userId, // Use IdentityUserId for frontend
                Timestamp = DateTime.UtcNow
            });

            await Clients.Caller.SendAsync("FriendRemoved", new
            {
                RemovedFriendId = friendId, // Use IdentityUserId for frontend
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

            // Convert IdentityUserId to MongoDB _id for database queries
            var sender = await _context.Users.Find(u => u.IdentityUserId == senderId).FirstOrDefaultAsync();
            if (sender == null)
            {
                await Clients.Caller.SendAsync("Error", "User not found");
                return;
            }

            var senderMongoId = sender.Id;

            // Validate chat access
            var chat = await _context.Chats.Find(c => c.Id == chatId).FirstOrDefaultAsync();
            if (chat == null || (!chat.Participants.Contains(senderMongoId)))
            {
                await Clients.Caller.SendAsync("Error", "Chat not found or access denied");
                return;
            }

            // Create message
            var newMessage = new Message
            {
                ChatId = chatId,
                SenderId = senderMongoId, // Store MongoDB _id
                Content = message,
                SentAt = DateTime.UtcNow
            };

            await _context.Messages.InsertOneAsync(newMessage);

            // Get participant IdentityUserIds for notifications
            var participants = await _context.Users
                .Find(u => chat.Participants.Contains(u.Id))
                .ToListAsync();

            var participantIdentityIds = participants.Select(u => u.IdentityUserId).ToList();

            // Notify all chat participants
            foreach (var participantIdentityId in participantIdentityIds)
            {
                if (participantIdentityId != senderId) // Don't send to sender
                {
                    await Clients.Group($"user_{participantIdentityId}").SendAsync("MessageReceived", new
                    {
                        ChatId = chatId,
                        MessageId = newMessage.Id,
                        SenderId = senderId, // Use IdentityUserId for frontend
                        SenderName = sender.UserName ?? "Unknown User",
                        SenderAvatar = sender.AvatarUrl,
                        Content = message,
                        Timestamp = newMessage.SentAt.ToString("O") // ISO 8601 format
                    });
                }
            }

            // Confirm message sent to sender with complete sender information
            await Clients.Caller.SendAsync("MessageSent", new
            {
                ChatId = chatId,
                MessageId = newMessage.Id,
                SenderId = senderId, // Use IdentityUserId for frontend
                SenderName = sender.UserName ?? "Unknown User",
                Content = message,
                Timestamp = newMessage.SentAt.ToString("O") // ISO 8601 format
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

            // Convert IdentityUserId to MongoDB _id for database queries
            var user = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            if (user == null)
            {
                await Clients.Caller.SendAsync("Error", "User not found");
                return;
            }

            var userMongoId = user.Id;

            var message = await _context.Messages.Find(m => m.Id == messageId).FirstOrDefaultAsync();
            if (message == null)
            {
                await Clients.Caller.SendAsync("Error", "Message not found");
                return;
            }

            // Check if user is participant in the chat
            var chat = await _context.Chats.Find(c => c.Id == message.ChatId).FirstOrDefaultAsync();
            if (chat == null || !chat.Participants.Contains(userMongoId))
            {
                await Clients.Caller.SendAsync("Error", "Access denied");
                return;
            }

            // Mark as read
            var readBy = new MessageRead
            {
                UserId = userMongoId, // Store MongoDB _id
                ReadAt = DateTime.UtcNow
            };
            var update = Builders<Message>.Update.Push(m => m.ReadBy, readBy);
            await _context.Messages.UpdateOneAsync(m => m.Id == messageId, update);

            // Get sender IdentityUserId for notification
            var sender = await _context.Users.Find(u => u.Id == message.SenderId).FirstOrDefaultAsync();
            var senderIdentityId = sender?.IdentityUserId;

            // Notify message sender
            if (!string.IsNullOrEmpty(senderIdentityId))
            {
                await Clients.Group($"user_{senderIdentityId}").SendAsync("MessageRead", new
                {
                    MessageId = messageId,
                    ReadBy = userId, // Use IdentityUserId for frontend
                    Timestamp = DateTime.UtcNow
                });
            }

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

            // Convert IdentityUserIds to MongoDB _ids for database queries
            var user = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            var friend = await _context.Users.Find(u => u.IdentityUserId == friendId).FirstOrDefaultAsync();
            
            if (user == null || friend == null)
            {
                await Clients.Caller.SendAsync("Error", "User not found");
                return;
            }

            var userMongoId = user.Id;
            var friendMongoId = friend.Id;

            // Check if chat already exists
            var existingChat = await _context.Chats
                .Find(c => c.Participants.Contains(userMongoId) && c.Participants.Contains(friendMongoId))
                .FirstOrDefaultAsync();

            if (existingChat != null)
            {
                // Convert MongoDB _ids back to IdentityUserIds for frontend
                var participants = new List<string> { userId, friendId };
                
                await Clients.Caller.SendAsync("ChatCreated", new
                {
                    ChatId = existingChat.Id,
                    Participants = participants, // Use IdentityUserIds for frontend
                    CreatedAt = existingChat.CreatedAt
                });
                return;
            }

            // Create new chat
            var newChat = new Chat
            {
                Participants = new List<string> { userMongoId, friendMongoId }, // Store MongoDB _ids
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            await _context.Chats.InsertOneAsync(newChat);

            // Convert MongoDB _ids back to IdentityUserIds for frontend
            var chatParticipants = new List<string> { userId, friendId };

            // Notify both users
            await Clients.Group($"user_{userId}").SendAsync("ChatCreated", new
            {
                ChatId = newChat.Id,
                Participants = chatParticipants, // Use IdentityUserIds for frontend
                CreatedAt = newChat.CreatedAt
            });

            await Clients.Group($"user_{friendId}").SendAsync("ChatCreated", new
            {
                ChatId = newChat.Id,
                Participants = chatParticipants, // Use IdentityUserIds for frontend
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
        // Debug: Log the request details
        var httpContext = Context.GetHttpContext();
        var accessToken = httpContext?.Request.Query["access_token"].FirstOrDefault();
        _logger.LogInformation($"Access token from query: {!string.IsNullOrEmpty(accessToken)}");
        if (!string.IsNullOrEmpty(accessToken))
        {
            _logger.LogInformation($"Access token length: {accessToken.Length}");
            _logger.LogInformation($"Access token preview: {accessToken.Substring(0, Math.Min(20, accessToken.Length))}...");
        }
        
        // Try to get from JWT token first
        var user = Context.User;
        _logger.LogInformation($"Context.User is null: {user == null}");
        if (user != null)
        {
            _logger.LogInformation($"User.Identity is null: {user.Identity == null}");
            if (user.Identity != null)
            {
                _logger.LogInformation($"User.Identity.IsAuthenticated: {user.Identity.IsAuthenticated}");
                _logger.LogInformation($"User.Identity.Name: {user.Identity.Name}");
            }
        }
        
        if (user?.Identity?.IsAuthenticated == true)
        {
            _logger.LogInformation($"User is authenticated: {user.Identity.Name}");
            _logger.LogInformation($"User claims: {string.Join(", ", user.Claims.Select(c => $"{c.Type}={c.Value}"))}");
            
            var userIdClaim = user.FindFirst("sub") ?? 
                             user.FindFirst("userId") ?? 
                             user.FindFirst("nameid") ?? 
                             user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
            if (userIdClaim != null)
            {
                _logger.LogInformation($"Found userId from JWT claim: {userIdClaim.Value}");
                return userIdClaim.Value;
            }
            else
            {
                _logger.LogWarning("User is authenticated but no userId claim found in JWT token");
            }
        }
        else
        {
            _logger.LogWarning($"User is not authenticated. Identity: {user?.Identity?.Name}, IsAuthenticated: {user?.Identity?.IsAuthenticated}");
        }

        // Fallback to query parameter or header (for development/testing)
        var userId = Context.GetHttpContext()?.Request.Query["userId"].FirstOrDefault();
        if (!string.IsNullOrEmpty(userId))
        {
            _logger.LogInformation($"Using userId from query parameter: {userId}");
            return userId;
        }
        
        userId = Context.GetHttpContext()?.Request.Headers["X-User-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(userId))
        {
            _logger.LogInformation($"Using userId from header: {userId}");
            return userId;
        }
        
        _logger.LogWarning("No userId found in JWT claims, query parameters, or headers");
        return null;
    }

    private async Task<Models.User?> CreateUserFromIdentityService(string identityUserId)
    {
        try
        {
            // Get Identity service URL from configuration
            var identityServiceUrl = Environment.GetEnvironmentVariable("IDENTITY_SERVICE_URL") ?? "http://localhost:5000";
            
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            // Get user info from Identity service
            var response = await httpClient.GetAsync($"{identityServiceUrl}/api/auth/users/{identityUserId}");
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failed to get user {identityUserId} from Identity service: {response.StatusCode}");
                return null;
            }
            
            var userJson = await response.Content.ReadAsStringAsync();
            var userData = System.Text.Json.JsonSerializer.Deserialize<IdentityUserData>(userJson);
            
            if (userData == null)
            {
                _logger.LogWarning($"Failed to deserialize user data for {identityUserId}");
                return null;
            }
            
            // Create user in MongoDB
            var newUser = new Models.User
            {
                IdentityUserId = identityUserId,
                UserName = userData.UserName ?? "Unknown",
                DisplayName = userData.DisplayName ?? "",
                Bio = "",
                AvatarUrl = null,
                IsPrivate = userData.IsPrivate,
                Playlists = new List<Models.PlaylistReference>(),
                FollowedUsers = new List<Models.UserReference>(),
                Followers = new List<Models.UserReference>(),
                CreatedAt = DateTime.UtcNow
            };
            
            await _context.Users.InsertOneAsync(newUser);
            _logger.LogInformation($"Created user {identityUserId} in MongoDB with MongoDB ID: {newUser.Id}");
            
            return newUser;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error creating user {identityUserId} from Identity service");
            return null;
        }
    }

    private async Task NotifyFriendsOnlineStatus(string userId, bool isOnline)
    {
        try
        {
            // Convert IdentityUserId to MongoDB _id for database queries
            var user = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            if (user == null)
            {
                _logger.LogWarning($"User not found for IdentityUserId: {userId}");
                return;
            }

            var userMongoId = user.Id;

            // Get user's friends using MongoDB _id
            var friends = await _context.Friends
                .Find(f => (f.UserId == userMongoId || f.FriendId == userMongoId) && f.Status == FriendStatus.Accepted)
                .ToListAsync();

            var friendMongoIds = friends.Select(f => f.UserId == userMongoId ? f.FriendId : f.UserId).ToList();

            // Get friend IdentityUserIds for SignalR groups
            var friendUsers = await _context.Users
                .Find(u => friendMongoIds.Contains(u.Id))
                .ToListAsync();

            var friendIdentityIds = friendUsers.Select(u => u.IdentityUserId).ToList();

            foreach (var friendIdentityId in friendIdentityIds)
            {
                await Clients.Group($"user_{friendIdentityId}").SendAsync("FriendStatusChanged", new
                {
                    FriendId = userId, // Use IdentityUserId for frontend
                    FriendName = user.UserName ?? "Unknown User",
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

            // Convert IdentityUserId to MongoDB _id for database queries
            var user = await _context.Users.Find(u => u.IdentityUserId == userId).FirstOrDefaultAsync();
            if (user == null)
            {
                _logger.LogWarning($"User not found for IdentityUserId: {userId}");
                await Clients.Caller.SendAsync("Error", "User not found");
                return;
            }

            var userMongoId = user.Id;

            // Get user's friends using MongoDB _id
            var friends = await _context.Friends
                .Find(f => (f.UserId == userMongoId || f.FriendId == userMongoId) && f.Status == FriendStatus.Accepted)
                .ToListAsync();

            var friendMongoIds = friends.Select(f => f.UserId == userMongoId ? f.FriendId : f.UserId).ToList();

            // Get friend IdentityUserIds for online status check
            var friendUsers = await _context.Users
                .Find(u => friendMongoIds.Contains(u.Id))
                .ToListAsync();

            var friendIdentityIds = friendUsers.Select(u => u.IdentityUserId).ToList();
            var onlineFriends = friendIdentityIds.Where(id => _userConnections.ContainsKey(id)).ToList();

            await Clients.Caller.SendAsync("OnlineFriends", onlineFriends);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting online friends");
            await Clients.Caller.SendAsync("Error", "Failed to get online friends");
        }
    }

}

// Helper class for deserializing Identity service response
public class IdentityUserData
{
    public string Id { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsPrivate { get; set; }
    public DateTime CreatedAt { get; set; }
} 