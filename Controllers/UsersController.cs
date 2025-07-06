using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Models;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly MongoDbContext _context;

    public UsersController(MongoDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers()
    {
        var users = await _context.Users
            .Find(_ => true)
            .ToListAsync();

        var userDtos = users.Select(u => new UserDto
        {
            Id = u.Id,
            IdentityUserId = u.IdentityUserId,
            UserName = u.UserName,
            Playlists = u.Playlists.Select(p => new PlaylistReferenceDto { Id = p.Id }).ToList(),
            FollowedUsers = u.FollowedUsers.Select(fu => new UserReferenceDto { Id = fu.Id }).ToList(),
            Followers = u.Followers.Select(f => new UserReferenceDto { Id = f.Id }).ToList(),
            IsPrivate = u.IsPrivate,
            CreatedAt = u.CreatedAt
        }).ToList();

        return Ok(userDtos);
    }

    [HttpGet("identity/{identityUserId}")]
    public async Task<ActionResult<UserDto>> GetUserByIdentityId(string identityUserId)
    {
        var user = await _context.Users
            .Find(u => u.IdentityUserId == identityUserId)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return NotFound();
        }

        var userDto = new UserDto
        {
            Id = user.Id,
            IdentityUserId = user.IdentityUserId,
            UserName = user.UserName,
            Playlists = user.Playlists.Select(p => new PlaylistReferenceDto { Id = p.Id }).ToList(),
            FollowedUsers = user.FollowedUsers.Select(fu => new UserReferenceDto { Id = fu.Id }).ToList(),
            Followers = user.Followers.Select(f => new UserReferenceDto { Id = f.Id }).ToList(),
            IsPrivate = user.IsPrivate,
            CreatedAt = user.CreatedAt
        };

        return Ok(userDto);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserDto>> GetUser(string id)
    {
        var user = await _context.Users
            .Find(u => u.Id == id)
            .FirstOrDefaultAsync();

        if (user == null)
        {
            return NotFound();
        }

        var userDto = new UserDto
        {
            Id = user.Id,
            IdentityUserId = user.IdentityUserId,
            UserName = user.UserName,
            Playlists = user.Playlists.Select(p => new PlaylistReferenceDto { Id = p.Id }).ToList(),
            FollowedUsers = user.FollowedUsers.Select(fu => new UserReferenceDto { Id = fu.Id }).ToList(),
            Followers = user.Followers.Select(f => new UserReferenceDto { Id = f.Id }).ToList(),
            IsPrivate = user.IsPrivate,
            CreatedAt = user.CreatedAt
        };

        return Ok(userDto);
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUser(CreateUserDto dto)
    {
        var user = new Models.User
        {
            IdentityUserId = dto.IdentityUserId,
            UserName = dto.UserName,
            IsPrivate = dto.IsPrivate
        };

        await _context.Users.InsertOneAsync(user);

        var userDto = new UserDto
        {
            Id = user.Id,
            IdentityUserId = user.IdentityUserId,
            UserName = user.UserName,
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
        var updateDefinition = Builders<Models.User>.Update
            .Set(u => u.UpdatedAt, DateTime.UtcNow);

        if (!string.IsNullOrEmpty(dto.UserName))
            updateDefinition = updateDefinition.Set(u => u.UserName, dto.UserName);

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

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        var result = await _context.Users.DeleteOneAsync(u => u.Id == id);

        if (result.DeletedCount == 0)
        {
            return NotFound();
        }

        return NoContent();
    }


}

public class UserDto
{
    public string Id { get; set; } = string.Empty;
    public string IdentityUserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
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
    public bool IsPrivate { get; set; } = false;
}

public class UpdateUserDto
{
    public string? UserName { get; set; }
    public bool? IsPrivate { get; set; }
} 