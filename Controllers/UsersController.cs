using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Models;
using User.Services;
namespace User.Controllers;
[ApiController, Route("api/users")]
public class UsersController(MongoDbContext db, ProfilePolicy profiles, IHttpClientFactory clients, IAzureBlobService blobs, HistoryService history, IConfiguration config, MongoTransactions transactions, INotificationService notifications, TimeProvider clock, ILogger<UsersController> logger) : ControllerBase
{
    [HttpGet("health"), AllowAnonymous] public object Health() => new { status = "alive", readiness = "/health/ready" };
    private async Task<object> Dto(Models.User user, bool summary = false, bool includeGraph = true)
    {
        var following = summary || !includeGraph ? [] : await db.Follows.Find(x => x.FollowerId == user.IdentityUserId).Limit(100).ToListAsync();
        var followers = summary || !includeGraph ? [] : await db.Follows.Find(x => x.FollowedId == user.IdentityUserId).Limit(100).ToListAsync();
        var followedIds = following.Select(x => x.FollowedId).ToList(); var followerIds = followers.Select(x => x.FollowerId).ToList();
        var related = summary || !includeGraph ? [] : await db.Users.Find(x => followedIds.Contains(x.IdentityUserId) || followerIds.Contains(x.IdentityUserId)).Limit(200).ToListAsync();
        return new { user.Id, user.IdentityUserId, user.UserName, displayName = summary ? null : user.DisplayName, bio = summary ? null : user.Bio, avatarUrl = summary ? null : user.AvatarUrl, user.IsPrivate, playlists = Array.Empty<object>(), followedUsers = related.Where(x => followedIds.Contains(x.IdentityUserId)).Select(x => new { x.Id }), followers = related.Where(x => followerIds.Contains(x.IdentityUserId)).Select(x => new { x.Id }), user.CreatedAt };
    }
    [HttpGet("all"), Authorize(Roles = "Admin")]
    public async Task<object> All(int limit = 100, int skip = 0) { Input.Page(limit, skip); var users = await db.Users.Find(_ => true).SortBy(x => x.Id).Skip(skip).Limit(limit).ToListAsync(); return await Task.WhenAll(users.Select(x => Dto(x, false, false))); }
    [HttpGet("identity/{identityUserId}"), HttpGet("{identityUserId}"), AllowAnonymous]
    public async Task<object> Get(string identityUserId) => await Dto(await profiles.Read(User, identityUserId, HttpContext.RequestAborted));
    [HttpGet("check/{identityUserId}")]
    public async Task<object> Check(string identityUserId) { Input.Owner(User, identityUserId); var user = await db.Users.Find(x => x.IdentityUserId == identityUserId).FirstOrDefaultAsync(); return new { exists = user != null, userId = identityUserId, mongoId = user?.Id, userName = user?.UserName }; }
    [HttpPost("batch")]
    public async Task<object> Batch(BatchUserRequest request)
    {
        if (request.UserIds.Count > 50) throw new ApiProblem(400, "Maximum batch size is 50.");
        var guids = request.UserIds.Where(x => Guid.TryParse(x, out _)).ToList(); var ids = request.UserIds.Where(x => MongoDB.Bson.ObjectId.TryParse(x, out _)).ToList();
        if (guids.Count + ids.Count != request.UserIds.Count) throw new ApiProblem(400, "Invalid profile identifier.");
        var users = await db.Users.Find(x => guids.Contains(x.IdentityUserId) || ids.Contains(x.Id)).Limit(50).ToListAsync();
        return await Task.WhenAll(users.Select(x => Dto(x, !ProfilePolicy.Visible(User, x), false)));
    }
    [HttpGet("search")]
    public async Task<object> Search(string q = "", int page = 1, int pageSize = 20)
    {
        Input.Page(pageSize, checked((page - 1) * pageSize)); q = Input.Text(q, 100); if (q.Length == 0) return Array.Empty<object>();
        var literal = new MongoDB.Bson.BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(q), "i");
        var filter = Builders<Models.User>.Filter.Regex(x => x.UserName, literal) | Builders<Models.User>.Filter.Regex(x => x.DisplayName, literal);
        var users = await db.Users.Find(filter).SortBy(x => x.UserName).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync();
        return await Task.WhenAll(users.Select(x => Dto(x, !ProfilePolicy.Visible(User, x), false)));
    }
    [HttpPost("sync-user/{identityUserId}")]
    public async Task<object> Sync(string identityUserId)
    {
        Input.Owner(User, identityUserId);
        using var response = await clients.CreateClient("Identity").GetAsync($"api/auth/internal/users/{identityUserId}");
        if (!response.IsSuccessStatusCode) throw new ApiProblem(503, "Account reconciliation unavailable.");
        var user = await response.Content.ReadFromJsonAsync<IdentityUserDto>() ?? throw new ApiProblem(503, "Invalid account contract.");
        await db.Users.UpdateOneAsync(x => x.IdentityUserId == user.Id, Builders<Models.User>.Update.SetOnInsert(x => x.IdentityUserId, user.Id).Set(x => x.UserName, user.UserName ?? "").Set(x => x.IsPrivate, user.IsPrivate ?? false).Set(x => x.Roles, user.Roles), new UpdateOptions { IsUpsert = true });
        return new { message = "Profile reconciled" };
    }
    [HttpPost("sync-users"), Authorize(Roles = "Admin")]
    public async Task<object> SyncAll()
    {
        using var response = await clients.CreateClient("Identity").GetAsync("api/auth/internal/users");
        if (!response.IsSuccessStatusCode) throw new ApiProblem(503, "Account reconciliation unavailable.");
        var envelope = await response.Content.ReadFromJsonAsync<IdentityUsers>() ?? throw new ApiProblem(503, "Invalid account contract.");
        foreach (var u in envelope.Users) await db.Users.UpdateOneAsync(x => x.IdentityUserId == u.Id, Builders<Models.User>.Update.SetOnInsert(x => x.IdentityUserId, u.Id).Set(x => x.UserName, u.UserName ?? "").Set(x => x.IsPrivate, u.IsPrivate ?? false).Set(x => x.Roles, u.Roles), new UpdateOptions { IsUpsert = true });
        return new { reconciled = envelope.Users.Count };
    }
    [HttpPut("identity/{id}"), HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new ApiProblem(400, "A profile object is required.");
        foreach (var field in new[] { "displayName", "bio", "avatarUrl", "userName" })
            if (patch.TryGetProperty(field, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ApiProblem(400, $"{field} must be text or null.");
        if (patch.TryGetProperty("isPrivate", out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ApiProblem(400, "isPrivate must be a boolean.");
        var user = await profiles.Own(User, id);
        var update = Builders<Models.User>.Update.Set(x => x.UpdatedAt, DateTime.UtcNow);
        if (patch.TryGetProperty("displayName", out var display)) update = update.Set(x => x.DisplayName, Input.Text(display.ValueKind == JsonValueKind.Null ? null : display.GetString(), 100));
        if (patch.TryGetProperty("bio", out var bio)) update = update.Set(x => x.Bio, Input.Text(bio.ValueKind == JsonValueKind.Null ? null : bio.GetString(), 1000));
        string? oldAvatar = null;
        if (patch.TryGetProperty("avatarUrl", out var avatar)) { if (avatar.ValueKind != JsonValueKind.Null && avatar.GetString() != "") throw new ApiProblem(400, "Use the validated avatar upload."); oldAvatar = user.AvatarUrl; update = update.Set(x => x.AvatarUrl, null); }
        if (patch.TryGetProperty("userName", out var name) || patch.TryGetProperty("isPrivate", out _))
        {
            var username = name.ValueKind == JsonValueKind.Undefined ? user.UserName : Input.Text(name.GetString(), 60, true);
            var isPrivate = patch.TryGetProperty("isPrivate", out var privacy) ? privacy.GetBoolean() : user.IsPrivate;
            using var response = await clients.CreateClient("Identity").PostAsJsonAsync("api/auth/internal/profile", new { identityUserId = user.IdentityUserId, userName = username, isPrivate });
            if (!response.IsSuccessStatusCode) throw new ApiProblem((int)response.StatusCode is 400 or 409 ? 409 : 503, "Account fields were not saved. Check the name or retry reconciliation.");
        }
        var filter = Builders<Models.User>.Filter.Eq(x => x.Id, user.Id);
        if (patch.TryGetProperty("avatarUrl", out _)) filter &= Builders<Models.User>.Filter.Eq(x => x.AvatarUrl, user.AvatarUrl);
        if ((await db.Users.UpdateOneAsync(filter, update)).MatchedCount != 1) throw new ApiProblem(409, "The profile changed concurrently. Reload before saving.");
        if (oldAvatar != null) await blobs.Cleanup(oldAvatar);
        return NoContent();
    }
    [HttpPost("identity/{id}/profile-picture"), HttpPost("{id}/profile-picture")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<object> Avatar(string id, IFormFile file)
    {
        var user = await profiles.Own(User, id);
        var url = await blobs.UploadUserProfilePictureAsync(user.IdentityUserId, file.OpenReadStream(), file.FileName);
        try { if ((await db.Users.UpdateOneAsync(x => x.Id == user.Id && x.AvatarUrl == user.AvatarUrl, Builders<Models.User>.Update.Set(x => x.AvatarUrl, url))).MatchedCount != 1) throw new ApiProblem(409, "The profile changed concurrently. Reload before uploading."); }
        catch { await blobs.Cleanup(url); throw; }
        await blobs.Published(url);
        if (user.AvatarUrl != null) await blobs.Cleanup(user.AvatarUrl);
        return new { avatarUrl = url, profilePictureUrl = url };
    }
    [HttpGet("avatar/{identityUserId}/{filename}"), AllowAnonymous]
    public async Task<IActionResult> AvatarBytes(string identityUserId, string filename)
    {
        var user = await profiles.Read(User, identityUserId);
        if (user.AvatarUrl == null || !user.AvatarUrl.EndsWith('/' + filename, StringComparison.Ordinal)) return NotFound();
        return File(await blobs.DownloadAvatar(identityUserId, filename), "image/png");
    }
    [HttpPost("identity/{id}/listening-history"), HttpPost("{id}/listening-history")]
    public async Task<object> Append(string id, AddListeningHistoryDto dto) { var user = await profiles.Own(User, id); await history.Append(user.IdentityUserId, dto, HttpContext.RequestAborted); return new { message = "Play saved" }; }
    [HttpGet("identity/{id}/listening-history"), HttpGet("{id}/listening-history"), AllowAnonymous]
    public async Task<object> History(string id, int limit = 50, int skip = 0) { var user = await profiles.Read(User, id); return await history.Read(user.IdentityUserId, limit, skip, HttpContext.RequestAborted); }
    [HttpGet("identity/{id}/top-artists/week/current"), AllowAnonymous]
    public async Task<object> Artists(string id) { var user = await profiles.Read(User, id); return await history.Artists(user.IdentityUserId, ct: HttpContext.RequestAborted); }
    [HttpDelete("{id}"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string id)
    {
        var user = await profiles.Find(id); using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/auth/users/{user.IdentityUserId}"); request.Headers.TryAddWithoutValidation("Authorization", Request.Headers.Authorization.ToString());
        using var result = await clients.CreateClient("Identity").SendAsync(request); return StatusCode((int)result.StatusCode);
    }
    [HttpDelete("internal/{identityUserId}"), AllowAnonymous]
    public async Task<IActionResult> Cleanup(string identityUserId)
    {
        if (!Input.Service(HttpContext, config)) return Unauthorized(); Input.GuidId(identityUserId);
        identityUserId = Guid.Parse(identityUserId).ToString();
        using var musicRequest = new HttpRequestMessage(HttpMethod.Delete, $"api/playlists/internal/owner/{identityUserId}");
        musicRequest.Headers.Add("X-Spotibuds-Service", config["ServiceAuth:Secret"]);
        using var musicResponse = await clients.CreateClient("Music").SendAsync(musicRequest);
        if (!musicResponse.IsSuccessStatusCode) throw new ApiProblem(503, "Playlist deletion is pending. Retry account reconciliation.");
        var result = await transactions.Run(async (session, ct) =>
        {
            var user = await db.Users.Find(session, x => x.IdentityUserId == identityUserId).FirstOrDefaultAsync(ct);
            var chats = new List<string>();
            if (user != null)
            {
                // Producers lock the same profiles before inserting their parent and notice.
                // A real write makes a concurrent insertion retry against the deleted profile.
                var previousUpdate = user.UpdatedAt ?? user.CreatedAt;
                var updated = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(clock.GetUtcNow().ToUnixTimeMilliseconds(), new DateTimeOffset(previousUpdate, TimeSpan.Zero).ToUnixTimeMilliseconds() + 1)).UtcDateTime;
                await db.Users.UpdateOneAsync(session, x => x.Id == user.Id, Builders<Models.User>.Update.Set(x => x.UpdatedAt, updated), cancellationToken: ct);
                chats = await db.Chats.Find(session, x => x.Participants.Contains(user.Id)).SortBy(x => x.Id).Limit(100).Project(x => x.Id).ToListAsync(ct);
            }
            var noticeFilter = Builders<Notification>.Filter.In("Data.chatId", chats);
            var progressing = user != null && await db.Chats.Find(session, x => x.Participants.Contains(user.Id) && !chats.Contains(x.Id)).AnyAsync(ct);
            if (!progressing)
            {
                var posts = await db.Feed.Find(session, x => x.IdentityUserId == identityUserId || x.WithIdentityUserId == identityUserId).Project(x => x.Id).ToListAsync(ct);
                noticeFilter |= Builders<Notification>.Filter.Eq(x => x.SourceUserId, identityUserId) | Builders<Notification>.Filter.Eq(x => x.TargetUserId, identityUserId) | Builders<Notification>.Filter.In("Data.postId", posts);
                await db.Reactions.DeleteManyAsync(session, x => x.FromIdentityUserId == identityUserId || x.ToIdentityUserId == identityUserId || (x.PostId != null && posts.Contains(x.PostId)), cancellationToken: ct);
                await db.Feed.DeleteManyAsync(session, x => x.IdentityUserId == identityUserId || x.WithIdentityUserId == identityUserId, cancellationToken: ct);
                await db.History.DeleteManyAsync(session, x => x.IdentityUserId == identityUserId, cancellationToken: ct);
                await db.Follows.DeleteManyAsync(session, x => x.FollowerId == identityUserId || x.FollowedId == identityUserId, cancellationToken: ct);
                if (user != null)
                {
                    await db.Friends.DeleteManyAsync(session, x => x.UserId == user.Id || x.FriendId == user.Id, cancellationToken: ct);
                    if (user.AvatarUrl != null)
                        await db.AvatarCleanup.UpdateOneAsync(session, x => x.Url == user.AvatarUrl, Builders<AvatarCleanupIntent>.Update.SetOnInsert(x => x.Url, user.AvatarUrl).Set(x => x.DueAt, clock.GetUtcNow().UtcDateTime), new UpdateOptions { IsUpsert = true }, ct);
                    await db.Users.DeleteOneAsync(session, x => x.Id == user.Id, cancellationToken: ct);
                }
            }
            var recipients = await db.Notifications.Find(session, noticeFilter).Project(x => x.TargetUserId).ToListAsync(ct);
            await db.Notifications.DeleteManyAsync(session, noticeFilter, cancellationToken: ct);
            await db.Messages.DeleteManyAsync(session, x => chats.Contains(x.ChatId), cancellationToken: ct);
            await db.Chats.DeleteManyAsync(session, x => chats.Contains(x.Id), cancellationToken: ct);
            return (Progressing: progressing, AvatarUrl: progressing ? null : user?.AvatarUrl, Recipients: recipients.Distinct().ToArray());
        }, HttpContext.RequestAborted);
        try { await Task.WhenAll(result.Recipients.Select(notifications.RefreshCountAsync)).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { logger.LogWarning("Committed account cleanup notification refresh is pending."); }
        if (result.Progressing) throw new ApiProblem(503, "Account cleanup is progressing in bounded batches. Reconciliation will retry.");
        if (result.AvatarUrl != null)
        {
            try { await blobs.Cleanup(result.AvatarUrl).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { logger.LogWarning("Committed account avatar cleanup will be retried from its durable intent."); }
        }
        return NoContent();
    }
    public class BatchUserRequest { public List<string> UserIds { get; set; } = []; }
    public class IdentityUsers { public List<IdentityUserDto> Users { get; set; } = []; }
}
public class IdentityUserDto { public string Id { get; set; } = ""; public string? UserName { get; set; } public bool? IsPrivate { get; set; } public List<string> Roles { get; set; } = []; }
public class AddListeningHistoryDto { public string SongId { get; set; } = ""; public string SongTitle { get; set; } = ""; public string Artist { get; set; } = ""; public string? CoverUrl { get; set; } public int Duration { get; set; } }
