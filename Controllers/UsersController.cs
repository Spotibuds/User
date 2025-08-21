using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Models;
using Microsoft.Extensions.Hosting;
using User.Services;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly MongoDbContext _context;
    private readonly IAzureBlobService _azureBlobService;

    public UsersController(MongoDbContext context, IAzureBlobService azureBlobService)
    {
        _context = context;
        _azureBlobService = azureBlobService;
    }

    [HttpGet("health")]
    public ActionResult<string> Health()
    {
        return Ok("User service is running!");
    }



    [HttpPost("sync-users")]
    public async Task<ActionResult<string>> SyncUsersFromIdentity()
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            // Get users from Identity service
            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri("http://localhost:5000/");
            
            var identityUsersResponse = await httpClient.GetAsync("api/auth/users");
            
            if (!identityUsersResponse.IsSuccessStatusCode)
            {
                var errorContent = await identityUsersResponse.Content.ReadAsStringAsync();
                return StatusCode(500, $"Failed to get users from Identity service. Status: {identityUsersResponse.StatusCode}");
            }

            var identityUsersJson = await identityUsersResponse.Content.ReadAsStringAsync();

            // Parse the JSON response from Identity service
            var identityUsers = System.Text.Json.JsonSerializer.Deserialize<List<IdentityUserDto>>(identityUsersJson);
            
            if (identityUsers == null || !identityUsers.Any())
            {
                return Ok("No users found in Identity service");
            }

            // Convert Identity users to User service users
            var usersToCreate = identityUsers.Select(identityUser => new Models.User
            {
                IdentityUserId = identityUser.Id,
                UserName = identityUser.UserName ?? "Unknown",
                DisplayName = identityUser.UserName ?? "Unknown",
                Bio = null,
                AvatarUrl = null,
                IsPrivate = identityUser.IsPrivate ?? false,
                Playlists = new List<PlaylistReference>(),
                FollowedUsers = new List<UserReference>(),
                Followers = new List<UserReference>(),
                CreatedAt = identityUser.CreatedAt ?? DateTime.UtcNow
            }).ToList();

            var createdCount = 0;
            var skippedCount = 0;

            foreach (var user in usersToCreate)
            {
                try
                {
                    // Check if user already exists by IdentityUserId (avoiding the ObjectId issue)
                    var filter = Builders<Models.User>.Filter.Eq(u => u.IdentityUserId, user.IdentityUserId);
                    var existingUser = await _context.Users.Find(filter).FirstOrDefaultAsync();

                    if (existingUser == null)
                    {
                        // Create new user
                        await _context.Users.InsertOneAsync(user);
                        createdCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue processing other users
                }
            }

            var totalUsers = await _context.Users.CountDocumentsAsync(_ => true);
            return Ok($"Sync completed! Created {createdCount} new users, skipped {skippedCount} existing users. Total users in database: {totalUsers}");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error syncing users: {ex.Message}");
        }
    }

    [HttpPost("sync-user/{identityUserId}")]
    public async Task<ActionResult<string>> SyncSpecificUser(string identityUserId)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            // Check if user already exists in MongoDB
            var existingUser = await _context.Users.Find(u => u.IdentityUserId == identityUserId).FirstOrDefaultAsync();
            if (existingUser != null)
            {
                return Ok($"User {identityUserId} already exists in MongoDB");
            }

            // Get user from Identity service
            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri("http://localhost:5000/");
            
            var response = await httpClient.GetAsync($"api/auth/users/{identityUserId}");
            
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(500, $"Failed to get user {identityUserId} from Identity service. Status: {response.StatusCode}");
            }

            var userJson = await response.Content.ReadAsStringAsync();
            var identityUser = System.Text.Json.JsonSerializer.Deserialize<IdentityUserDto>(userJson);
            
            if (identityUser == null)
            {
                return StatusCode(500, $"Failed to deserialize user data for {identityUserId}");
            }

            // Create user in MongoDB
            var newUser = new Models.User
            {
                IdentityUserId = identityUser.Id,
                UserName = identityUser.UserName ?? "Unknown",
                DisplayName = identityUser.UserName ?? "Unknown",
                Bio = null,
                AvatarUrl = null,
                IsPrivate = identityUser.IsPrivate ?? false,
                Playlists = new List<PlaylistReference>(),
                FollowedUsers = new List<UserReference>(),
                Followers = new List<UserReference>(),
                CreatedAt = identityUser.CreatedAt ?? DateTime.UtcNow
            };

            await _context.Users.InsertOneAsync(newUser);
            
            return Ok($"Successfully created user {identityUserId} in MongoDB with MongoDB ID: {newUser.Id}");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error syncing user {identityUserId}: {ex.Message}");
        }
    }

    [HttpGet("check/{identityUserId}")]
    public async Task<ActionResult<object>> CheckUserExists(string identityUserId)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var user = await _context.Users.Find(u => u.IdentityUserId == identityUserId).FirstOrDefaultAsync();
            
            return Ok(new
            {
                exists = user != null,
                userId = identityUserId,
                mongoId = user?.Id,
                userName = user?.UserName
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error checking user {identityUserId}: {ex.Message}");
        }
    }

    [HttpGet("all")]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers()
    {
                if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        const int maxRetries = 3;
        var retryCount = 0;

        while (retryCount < maxRetries)
        {
            try
            {
                var users = await _context.Users
                    .Find(_ => true)
                    .ToListAsync();

                var userDtos = users.Select(u => new UserDto
                {
                    Id = u.Id,
                    IdentityUserId = u.IdentityUserId,
                    UserName = u.UserName,
                    DisplayName = u.DisplayName,
                    Bio = u.Bio,
                    AvatarUrl = u.AvatarUrl,
                    Playlists = u.Playlists.Select(p => new PlaylistReferenceDto { Id = p.Id }).ToList(),
                    FollowedUsers = u.FollowedUsers.Select(f => new UserReferenceDto { Id = f.Id }).ToList(),
                    Followers = u.Followers.Select(f => new UserReferenceDto { Id = f.Id }).ToList(),
                    IsPrivate = u.IsPrivate,
                    CreatedAt = u.CreatedAt
                }).ToList();

                return Ok(userDtos);
            }
            catch (MongoDB.Driver.MongoConnectionException ex)
            {
                retryCount++;
                
                if (retryCount >= maxRetries)
                {
                    return StatusCode(503, "Database connection failed. Please try again later.");
                }
                
                // Wait before retrying
                await Task.Delay(1000 * retryCount);
            }
            catch (System.TimeoutException ex)
            {
                retryCount++;
                
                if (retryCount >= maxRetries)
                {
                    return StatusCode(503, "Database connection failed. Please try again later.");
                }
                
                // Wait before retrying
                await Task.Delay(1000 * retryCount);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Internal server error");
            }
        }

        return StatusCode(503, "Database connection failed after multiple attempts. Please try again later.");
    }

    [HttpGet("identity/{identityUserId}")]
    public async Task<ActionResult<UserDto>> GetUserByIdentityId(string identityUserId)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var user = await _context.Users
                .Find(u => u.IdentityUserId == identityUserId)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                return NotFound($"User with identity ID '{identityUserId}' not found");
            }

            var userDto = new UserDto
            {
                Id = user.Id,
                IdentityUserId = user.IdentityUserId,
                UserName = user.UserName,
                DisplayName = user.DisplayName,
                Bio = user.Bio,
                AvatarUrl = user.AvatarUrl,
                Playlists = user.Playlists.Select(p => new PlaylistReferenceDto { Id = p.Id }).ToList(),
                FollowedUsers = user.FollowedUsers.Select(fu => new UserReferenceDto { Id = fu.Id }).ToList(),
                Followers = user.Followers.Select(f => new UserReferenceDto { Id = f.Id }).ToList(),
                IsPrivate = user.IsPrivate,
                CreatedAt = user.CreatedAt
            };

            return Ok(userDto);
        }
        catch (MongoDB.Driver.MongoConnectionException)
        {
            return StatusCode(503, "Database connection failed. Please try again later.");
        }
        catch (Exception)
        {
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserDto>> GetUser(string id)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            // Try to find user by IdentityUserId first, then by MongoDB _id
            var user = await _context.Users
                .Find(u => u.IdentityUserId == id)
                .FirstOrDefaultAsync();
            
            if (user == null)
            {
                // If not found by IdentityUserId, try by MongoDB _id
                user = await _context.Users
                    .Find(u => u.Id == id)
                    .FirstOrDefaultAsync();
            }
            
            if (user == null)
            {
                return NotFound($"User with id '{id}' not found");
            }

            var userDto = new UserDto
            {
                Id = user.Id,
                IdentityUserId = user.IdentityUserId,
                UserName = user.UserName,
                DisplayName = user.DisplayName,
                Bio = user.Bio,
                AvatarUrl = user.AvatarUrl,
                Playlists = user.Playlists.Select(p => new PlaylistReferenceDto { Id = p.Id }).ToList(),
                FollowedUsers = user.FollowedUsers.Select(fu => new UserReferenceDto { Id = fu.Id }).ToList(),
                Followers = user.Followers.Select(f => new UserReferenceDto { Id = f.Id }).ToList(),
                IsPrivate = user.IsPrivate,
                CreatedAt = user.CreatedAt
            };

            return Ok(userDto);
        }
        catch (MongoDB.Driver.MongoConnectionException)
        {
            return StatusCode(503, "Database connection failed. Please try again later.");
        }
        catch (Exception)
        {
            return StatusCode(500, "Internal server error");
        }
    }











    [HttpGet("search")]
    public async Task<ActionResult<List<UserDto>>> SearchUsers(string q = "", int page = 1, int pageSize = 20)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Ok(new List<UserDto>());
            }

            var queryLower = q.ToLower();
            
            // Search users by username or display name
            var users = await _context.Users
                .Find(u => 
                    u.UserName.ToLower().Contains(queryLower) || 
                    (u.DisplayName != null && u.DisplayName.ToLower().Contains(queryLower))
                )
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync();

            var userDtos = users.Select(user => new UserDto
            {
                Id = user.Id,
                IdentityUserId = user.IdentityUserId,
                UserName = user.UserName,
                DisplayName = user.DisplayName,
                Bio = user.Bio,
                AvatarUrl = user.AvatarUrl,
                Playlists = user.Playlists.Select(p => new PlaylistReferenceDto { Id = p.Id }).ToList(),
                FollowedUsers = user.FollowedUsers.Select(fu => new UserReferenceDto { Id = fu.Id }).ToList(),
                Followers = user.Followers.Select(f => new UserReferenceDto { Id = f.Id }).ToList(),
                IsPrivate = user.IsPrivate,
                CreatedAt = user.CreatedAt
            }).ToList();

            return Ok(userDtos);
        }
        catch (MongoDB.Driver.MongoConnectionException)
        {
            return StatusCode(503, "Database connection failed. Please try again later.");
        }
        catch (Exception)
        {
            return StatusCode(500, "Error searching users");
        }
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUser(CreateUserDto dto)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        var user = new Models.User
        {
            IdentityUserId = dto.IdentityUserId,
            UserName = dto.UserName,
            DisplayName = dto.DisplayName,
            Bio = dto.Bio,
            AvatarUrl = dto.AvatarUrl,
            IsPrivate = dto.IsPrivate
        };

        await _context.Users.InsertOneAsync(user);

        var userDto = new UserDto
        {
            Id = user.Id,
            IdentityUserId = user.IdentityUserId,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            Bio = user.Bio,
            AvatarUrl = user.AvatarUrl,
            Playlists = user.Playlists.Select(p => new PlaylistReferenceDto { Id = p.Id }).ToList(),
            FollowedUsers = user.FollowedUsers.Select(fu => new UserReferenceDto { Id = fu.Id }).ToList(),
            Followers = user.Followers.Select(f => new UserReferenceDto { Id = f.Id }).ToList(),
            IsPrivate = user.IsPrivate,
            CreatedAt = user.CreatedAt
        };

        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, userDto);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUser(string id, UpdateUserDto dto)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        var updateDefinition = Builders<Models.User>.Update
            .Set(u => u.UpdatedAt, DateTime.UtcNow);

        if (!string.IsNullOrEmpty(dto.UserName))
            updateDefinition = updateDefinition.Set(u => u.UserName, dto.UserName);

        if (!string.IsNullOrEmpty(dto.DisplayName))
            updateDefinition = updateDefinition.Set(u => u.DisplayName, dto.DisplayName);

        if (!string.IsNullOrEmpty(dto.Bio))
            updateDefinition = updateDefinition.Set(u => u.Bio, dto.Bio);

        if (!string.IsNullOrEmpty(dto.AvatarUrl))
            updateDefinition = updateDefinition.Set(u => u.AvatarUrl, dto.AvatarUrl);

        if (dto.IsPrivate.HasValue)
            updateDefinition = updateDefinition.Set(u => u.IsPrivate, dto.IsPrivate.Value);

        var result = await _context.Users.UpdateOneAsync(
            u => u.Id == id,
            updateDefinition);

        if (result.MatchedCount == 0)
        {
            return NotFound();
        }

        return NoContent();
    }

    [HttpPut("identity/{identityUserId}")]
    public async Task<IActionResult> UpdateUserByIdentityId(string identityUserId, UpdateUserDto dto)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        var updateDefinition = Builders<Models.User>.Update
            .Set(u => u.UpdatedAt, DateTime.UtcNow);

        if (!string.IsNullOrEmpty(dto.UserName))
            updateDefinition = updateDefinition.Set(u => u.UserName, dto.UserName);

        if (!string.IsNullOrEmpty(dto.DisplayName))
            updateDefinition = updateDefinition.Set(u => u.DisplayName, dto.DisplayName);

        if (dto.Bio != null) // Allow setting empty bio
            updateDefinition = updateDefinition.Set(u => u.Bio, dto.Bio);

        if (!string.IsNullOrEmpty(dto.AvatarUrl))
            updateDefinition = updateDefinition.Set(u => u.AvatarUrl, dto.AvatarUrl);

        if (dto.IsPrivate.HasValue)
            updateDefinition = updateDefinition.Set(u => u.IsPrivate, dto.IsPrivate.Value);

        var result = await _context.Users.UpdateOneAsync(
            u => u.IdentityUserId == identityUserId,
            updateDefinition);

        if (result.MatchedCount == 0)
        {
            return NotFound();
        }

        return NoContent();
    }

    [HttpPost("{id}/profile-picture")]
    public async Task<IActionResult> UploadProfilePicture(string id, IFormFile file)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }

        // Validate file type
        var allowedTypes = new[] { "image/jpeg", "image/jpg", "image/png", "image/webp" };
        if (!allowedTypes.Contains(file.ContentType.ToLower()))
        {
            return BadRequest("Only JPEG, PNG, and WebP images are allowed");
        }

        // Validate file size (max 5MB)
        if (file.Length > 5 * 1024 * 1024)
        {
            return BadRequest("File size cannot exceed 5MB");
        }

        try
        {
            // Check if user exists
            var user = await _context.Users.Find(u => u.Id == id).FirstOrDefaultAsync();
            if (user == null)
            {
                return NotFound("User not found");
            }

            // Upload to Azure Blob Storage
            using var stream = file.OpenReadStream();
            var profilePictureUrl = await _azureBlobService.UploadUserProfilePictureAsync(id, stream, file.FileName);

            // Update user's avatar URL in database
            var updateDefinition = Builders<Models.User>.Update
                .Set(u => u.AvatarUrl, profilePictureUrl)
                .Set(u => u.UpdatedAt, DateTime.UtcNow);

            var result = await _context.Users.UpdateOneAsync(
                u => u.Id == id,
                updateDefinition);

            if (result.MatchedCount == 0)
            {
                return NotFound("User not found");
            }

            return Ok(new { avatarUrl = profilePictureUrl });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error uploading profile picture: {ex.Message}");
        }
    }

    [HttpPost("identity/{identityUserId}/profile-picture")]
    public async Task<IActionResult> UploadProfilePictureByIdentityId(string identityUserId, IFormFile file)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file provided");
        }

        // Validate file type
        var allowedTypes = new[] { "image/jpeg", "image/jpg", "image/png", "image/webp" };
        if (!allowedTypes.Contains(file.ContentType.ToLower()))
        {
            return BadRequest("Only JPEG, PNG, and WebP images are allowed");
        }

        // Validate file size (max 5MB)
        if (file.Length > 5 * 1024 * 1024)
        {
            return BadRequest("File size cannot exceed 5MB");
        }

        try
        {
            // Find user by identity user ID
            var user = await _context.Users.Find(u => u.IdentityUserId == identityUserId).FirstOrDefaultAsync();
            if (user == null)
            {
                return NotFound("User not found");
            }

            // Upload to Azure Blob Storage using the user's MongoDB ID
            using var stream = file.OpenReadStream();
            var profilePictureUrl = await _azureBlobService.UploadUserProfilePictureAsync(user.Id, stream, file.FileName);

            // Update user's avatar URL in database
            var updateDefinition = Builders<Models.User>.Update
                .Set(u => u.AvatarUrl, profilePictureUrl)
                .Set(u => u.UpdatedAt, DateTime.UtcNow);

            var result = await _context.Users.UpdateOneAsync(
                u => u.IdentityUserId == identityUserId,
                updateDefinition);

            if (result.MatchedCount == 0)
            {
                return NotFound("User not found");
            }

            return Ok(new { avatarUrl = profilePictureUrl });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error uploading profile picture: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        var result = await _context.Users.DeleteOneAsync(u => u.Id == id);

        if (result.DeletedCount == 0)
        {
            return NotFound();
        }

        return NoContent();
    }

    // Listening History endpoints
    [HttpPost("{userId}/listening-history")]
    public async Task<ActionResult> AddToListeningHistory(string userId, [FromBody] AddListeningHistoryDto dto)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var historyItem = new ListeningHistoryItem
            {
                SongId = dto.SongId,
                SongTitle = dto.SongTitle,
                Artist = dto.Artist,
                CoverUrl = dto.CoverUrl,
                PlayedAt = DateTime.UtcNow,
                Duration = dto.Duration
            };

            var filter = Builders<Models.User>.Filter.Eq(u => u.Id, userId);
            
            // First, add the item to the history
            var pushUpdate = Builders<Models.User>.Update
                .Push(u => u.ListeningHistory, historyItem);

            await _context.Users.UpdateOneAsync(filter, pushUpdate);

            // Then, limit to last 100 items by removing older items
            var user = await _context.Users.Find(filter).FirstOrDefaultAsync();
            if (user != null && user.ListeningHistory.Count > 100)
            {
                var limitedHistory = user.ListeningHistory
                    .OrderByDescending(h => h.PlayedAt)
                    .Take(100)
                    .ToList();
                
                var replaceUpdate = Builders<Models.User>.Update
                    .Set(u => u.ListeningHistory, limitedHistory);
                
                await _context.Users.UpdateOneAsync(filter, replaceUpdate);
            }

            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error adding to listening history: {ex.Message}");
        }
    }

    [HttpPost("identity/{identityUserId}/listening-history")]
    public async Task<ActionResult> AddToListeningHistoryByIdentity(string identityUserId, [FromBody] AddListeningHistoryDto dto)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var historyItem = new ListeningHistoryItem
            {
                SongId = dto.SongId,
                SongTitle = dto.SongTitle,
                Artist = dto.Artist,
                CoverUrl = dto.CoverUrl,
                PlayedAt = DateTime.UtcNow,
                Duration = dto.Duration
            };

            var filter = Builders<Models.User>.Filter.Eq(u => u.IdentityUserId, identityUserId);
            
            // First, add the item to the history
            var pushUpdate = Builders<Models.User>.Update
                .Push(u => u.ListeningHistory, historyItem);

            await _context.Users.UpdateOneAsync(filter, pushUpdate);

            // Then, limit to last 100 items by removing older items
            var user = await _context.Users.Find(filter).FirstOrDefaultAsync();
            if (user != null && user.ListeningHistory.Count > 100)
            {
                var limitedHistory = user.ListeningHistory
                    .OrderByDescending(h => h.PlayedAt)
                    .Take(100)
                    .ToList();
                
                var replaceUpdate = Builders<Models.User>.Update
                    .Set(u => u.ListeningHistory, limitedHistory);
                
                await _context.Users.UpdateOneAsync(filter, replaceUpdate);
            }

            // Persist a recent_song feed item with stable key and trim to last N per user
            if (_context.Feed != null)
            {
                var itemKey = $"recent:{identityUserId}:{dto.SongId}";
                var feedItem = new User.Entities.FeedItem
                {
                    IdentityUserId = identityUserId,
                    Type = "recent_song",
                    SongId = dto.SongId,
                    SongTitle = dto.SongTitle,
                    Artist = dto.Artist,
                    CoverUrl = dto.CoverUrl,
                    PlayedAt = historyItem.PlayedAt,
                    Key = itemKey,
                    CreatedAt = DateTime.UtcNow
                };

                // Upsert by key+identity to avoid duplicates if repeated quickly
                var upsertFilter = Builders<User.Entities.FeedItem>.Filter.And(
                    Builders<User.Entities.FeedItem>.Filter.Eq(f => f.IdentityUserId, identityUserId),
                    Builders<User.Entities.FeedItem>.Filter.Eq(f => f.Key, itemKey)
                );
                var updateDef = Builders<User.Entities.FeedItem>.Update
                    .Set(f => f.IdentityUserId, feedItem.IdentityUserId)
                    .Set(f => f.Type, feedItem.Type)
                    .Set(f => f.SongId, feedItem.SongId)
                    .Set(f => f.SongTitle, feedItem.SongTitle)
                    .Set(f => f.Artist, feedItem.Artist)
                    .Set(f => f.CoverUrl, feedItem.CoverUrl)
                    .Set(f => f.PlayedAt, feedItem.PlayedAt)
                    .Set(f => f.Key, feedItem.Key)
                    .Set(f => f.CreatedAt, feedItem.CreatedAt);
                await _context.Feed.UpdateOneAsync(upsertFilter, updateDef, new UpdateOptions { IsUpsert = true });

                // Trim to last N recent_song items per user (configurable via env FEED_RECENT_MAX)
                int maxRecent = 20;
                var envMax = Environment.GetEnvironmentVariable("FEED_RECENT_MAX");
                if (!string.IsNullOrEmpty(envMax) && int.TryParse(envMax, out var parsed) && parsed > 0 && parsed <= 200)
                {
                    maxRecent = parsed;
                }
                var recentItems = await _context.Feed
                    .Find(f => f.IdentityUserId == identityUserId && f.Type == "recent_song")
                    .SortByDescending(f => f.CreatedAt)
                    .Skip(maxRecent)
                    .ToListAsync();
                if (recentItems.Count > 0)
                {
                    var idsToDelete = recentItems.Select(r => r.Id).ToList();
                    await _context.Feed.DeleteManyAsync(f => idsToDelete.Contains(f.Id));
                }
            }

            return Ok(new { success = true, message = "Added to listening history" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error adding to listening history: {ex.Message}");
        }
    }

    [HttpGet("{userId}/listening-history")]
    public async Task<ActionResult<List<ListeningHistoryItem>>> GetListeningHistory(string userId, int limit = 50, int skip = 0)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var filter = Builders<Models.User>.Filter.Eq(u => u.Id, userId);
            var projection = Builders<Models.User>.Projection.Include(u => u.ListeningHistory);
            
            var user = await _context.Users.Find(filter).Project<Models.User>(projection).FirstOrDefaultAsync();
            
            if (user == null)
            {
                return NotFound("User not found");
            }

            var history = user.ListeningHistory
                .OrderByDescending(h => h.PlayedAt)
                .Skip(skip)
                .Take(limit)
                .ToList();

            return Ok(history);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error retrieving listening history: {ex.Message}");
        }
    }

    [HttpGet("identity/{identityUserId}/listening-history")]
    public async Task<ActionResult<List<ListeningHistoryItem>>> GetListeningHistoryByIdentity(string identityUserId, int limit = 50, int skip = 0)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var filter = Builders<Models.User>.Filter.Eq(u => u.IdentityUserId, identityUserId);
            var projection = Builders<Models.User>.Projection.Include(u => u.ListeningHistory);
            
            var user = await _context.Users.Find(filter).Project<Models.User>(projection).FirstOrDefaultAsync();
            
            if (user == null)
            {
                return NotFound("User not found");
            }

            var history = user.ListeningHistory
                .OrderByDescending(h => h.PlayedAt)
                .Skip(skip)
                .Take(limit)
                .ToList();

            return Ok(history);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error retrieving listening history: {ex.Message}");
        }
    }

    // Top Artists (current week)
    [HttpGet("identity/{identityUserId}/top-artists/week/current")]
    public async Task<ActionResult<object>> GetTopArtistsForCurrentWeek(string identityUserId)
    {
        if (!_context.IsConnected || _context.Users == null)
        {
            return StatusCode(503, "Service unavailable - database connection failed");
        }

        try
        {
            var filter = Builders<Models.User>.Filter.Eq(u => u.IdentityUserId, identityUserId);
            var projection = Builders<Models.User>.Projection
                .Include(u => u.TopArtistsWeekStart)
                .Include(u => u.TopArtistsCurrentWeek)
                .Include(u => u.ListeningHistory);

            var user = await _context.Users.Find(filter).Project<Models.User>(projection).FirstOrDefaultAsync();
            if (user == null)
            {
                return NotFound("User not found");
            }

            var currentWeekStart = GetCurrentWeekStartUtc();
            bool cacheValid = user.TopArtistsWeekStart.HasValue && user.TopArtistsWeekStart.Value == currentWeekStart && user.TopArtistsCurrentWeek != null && user.TopArtistsCurrentWeek.Count > 0;
            // If cache is valid and at least one artist has count > 1, trust cache; else recompute
            if (cacheValid && user.TopArtistsCurrentWeek.Any(a => a.Count > 1))
            {
                return Ok(user.TopArtistsCurrentWeek.OrderByDescending(a => a.Count).Take(3));
            }

            // Compute from listening history for current week
            var weekItems = user.ListeningHistory
                .Where(h => h.PlayedAt >= currentWeekStart)
                .ToList();

            // Count plays per individual artist (split by comma)
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in weekItems)
            {
                if (string.IsNullOrWhiteSpace(item.Artist)) continue;
                var parts = item.Artist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var p in parts)
                {
                    var key = p.Trim();
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (!dict.ContainsKey(key)) dict[key] = 0;
                    dict[key]++;
                }
            }

            var top = dict
                .Select(kv => new TopArtist { Name = kv.Key, Count = kv.Value })
                .OrderByDescending(t => t.Count)
                .ThenBy(t => t.Name)
                .Take(3)
                .ToList();

            // Save cache
            var update = Builders<Models.User>.Update
                .Set(u => u.TopArtistsWeekStart, currentWeekStart)
                .Set(u => u.TopArtistsCurrentWeek, top);
            await _context.Users.UpdateOneAsync(filter, update);

            return Ok(top);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error computing top artists: {ex.Message}");
        }
    }

    [NonAction]
    private static DateTime GetCurrentWeekStartUtc()
    {
        // Week starts Sunday 00:00 UTC
        var now = DateTime.UtcNow;
        int diff = (int)now.DayOfWeek; // Sunday=0
        var start = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-diff);
        return start;
    }


}

public class UserDto
{
    public string Id { get; set; } = string.Empty;
    public string IdentityUserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public List<PlaylistReferenceDto> Playlists { get; set; } = new();
    public List<UserReferenceDto> FollowedUsers { get; set; } = new();
    public List<UserReferenceDto> Followers { get; set; } = new();
    public bool IsPrivate { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PlaylistReferenceDto
{
    public string Id { get; set; } = string.Empty;
}

public class UserReferenceDto
{
    public string Id { get; set; } = string.Empty;
}

public class CreateUserDto
{
    public string IdentityUserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public bool IsPrivate { get; set; } = false;
}

public class UpdateUserDto
{
    public string? UserName { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public bool? IsPrivate { get; set; }
}

public class IdentityUserDto
{
    public string Id { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public bool? IsPrivate { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public class AddListeningHistoryDto
{
    public string SongId { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public int Duration { get; set; } = 0; // Duration listened in seconds
}