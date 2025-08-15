using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Models;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FeedController : ControllerBase
{
	private readonly MongoDbContext _context;

	public FeedController(MongoDbContext context)
	{
		_context = context;
	}

	[HttpGet("slides")]
	public async Task<ActionResult<object>> GetSlides([FromQuery] string identityUserId, [FromQuery] int limit = 20, [FromQuery] int skip = 0)
	{
		if (!_context.IsConnected || _context.Users == null)
		{
			return StatusCode(503, "Service unavailable - database connection failed");
		}

		try
		{
			// Get current user first
			var currentUser = await _context.Users
				.Find(u => u.IdentityUserId == identityUserId)
				.FirstOrDefaultAsync();

			if (currentUser == null)
			{
				return NotFound("User not found");
			}

			// Get other users who are not private and not the current user
			var otherUsers = await _context.Users
				.Find(u => !u.IsPrivate && u.IdentityUserId != identityUserId && u.ListeningHistory != null && u.ListeningHistory.Count > 0)
				.Project<Models.User>(Builders<Models.User>.Projection
					.Include(u => u.IdentityUserId)
					.Include(u => u.UserName)
					.Include(u => u.DisplayName)
					.Include(u => u.ListeningHistory)
					.Include(u => u.TopArtistsCurrentWeek)
				)
				.ToListAsync();

			if (!otherUsers.Any())
			{
				return Ok(new List<object>());
			}

			// Shuffle users for randomness
			var rng = new Random();
			otherUsers = otherUsers.OrderBy(_ => rng.Next()).ToList();

			var slides = new List<object>();
			var weekStart = DateTime.UtcNow.AddDays(-7);

			foreach (var user in otherUsers.Take(50)) // Limit to prevent too many slides
			{
				// 1) Recent song slide (only if they have recent activity)
				var recent = user.ListeningHistory?
					.OrderByDescending(h => h.PlayedAt)
					.FirstOrDefault();

				if (recent != null && recent.PlayedAt >= DateTime.UtcNow.AddDays(-30)) // Only show recent songs
				{
					slides.Add(new {
						type = "recent_song",
						identityUserId = user.IdentityUserId,
						username = user.UserName,
						displayName = user.DisplayName,
						songId = recent.SongId,
						songTitle = recent.SongTitle,
						artist = recent.Artist,
						coverUrl = recent.CoverUrl,
						playedAt = recent.PlayedAt
					});
				}

				// 2) Weekly top artists slide (only if they have current week data)
				if (user.TopArtistsCurrentWeek != null && user.TopArtistsCurrentWeek.Count > 0)
				{
					var currentWeekArtists = user.TopArtistsCurrentWeek
						.Where(a => a.Count > 0)
						.OrderByDescending(a => a.Count)
						.Take(3)
						.ToList();

					if (currentWeekArtists.Any())
					{
						slides.Add(new {
							type = "top_artists_week",
							identityUserId = user.IdentityUserId,
							username = user.UserName,
							displayName = user.DisplayName,
							topArtists = currentWeekArtists.Select(a => new { name = a.Name, count = a.Count }).ToList()
						});
					}
				}

				// 3) Weekly top songs slide (computed on the fly)
				var weekItems = user.ListeningHistory?
					.Where(h => h.PlayedAt >= weekStart)
					.ToList() ?? new List<ListeningHistoryItem>();

				if (weekItems.Count > 0)
				{
					var songMap = new Dictionary<string, (string? SongId, string? SongTitle, string? Artist, int Count)>(StringComparer.OrdinalIgnoreCase);
					foreach (var item in weekItems)
					{
						var key = !string.IsNullOrWhiteSpace(item.SongId) ? item.SongId! : (item.SongTitle ?? "unknown");
						if (!songMap.ContainsKey(key))
						{
							songMap[key] = (item.SongId, item.SongTitle, item.Artist, 0);
						}
						songMap[key] = (songMap[key].SongId, songMap[key].SongTitle, songMap[key].Artist, songMap[key].Count + 1);
					}
					var topSongs = songMap
						.Select(kv => new { songId = kv.Value.SongId, songTitle = kv.Value.SongTitle, artist = kv.Value.Artist, count = kv.Value.Count })
						.OrderByDescending(s => s.count)
						.ThenBy(s => s.songTitle)
						.Take(3)
						.ToList();
					if (topSongs.Count > 0)
					{
						slides.Add(new {
							type = "top_songs_week",
							identityUserId = user.IdentityUserId,
							username = user.UserName,
							displayName = user.DisplayName,
							topSongs = topSongs
						});
					}
				}
			}

			// Common artists slide with current user (only if current user has listening history)
			if (currentUser.ListeningHistory != null && currentUser.ListeningHistory.Count > 0)
			{
				var myArtists = new HashSet<string>(
					currentUser.ListeningHistory
						.SelectMany(h => (h.Artist ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
						.Where(a => !string.IsNullOrWhiteSpace(a))
						.Select(a => a.Trim()), 
					StringComparer.OrdinalIgnoreCase
				);

				foreach (var user in otherUsers.Take(20)) // Limit for performance
				{
					if (user.ListeningHistory == null || user.ListeningHistory.Count == 0) continue;

					var theirArtists = new HashSet<string>(
						user.ListeningHistory
							.SelectMany(h => (h.Artist ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
							.Where(a => !string.IsNullOrWhiteSpace(a))
							.Select(a => a.Trim()), 
						StringComparer.OrdinalIgnoreCase
					);

					var common = myArtists.Intersect(theirArtists, StringComparer.OrdinalIgnoreCase).Take(5).ToList();
					if (common.Count >= 2)
					{
						slides.Add(new {
							type = "common_artists",
							identityUserId = user.IdentityUserId,
							username = user.UserName,
							displayName = user.DisplayName,
							withIdentityUserId = identityUserId,
							commonArtists = common
						});
					}
				}
			}

			// Shuffle the slides and paginate
			slides = slides.OrderBy(_ => rng.Next()).Skip(skip).Take(limit).ToList();
			return Ok(slides);
		}
		catch (Exception ex)
		{
			return StatusCode(500, $"Error generating slides: {ex.Message}");
		}
	}

	[HttpPost("reactions")]
	public async Task<ActionResult<object>> SendReaction([FromBody] Reaction request)
	{
		if (!_context.IsConnected)
		{
			return StatusCode(503, "Service unavailable - database connection failed");
		}
		try
		{
			request.CreatedAt = DateTime.UtcNow;
			await _context.Reactions!.InsertOneAsync(request);
			return Ok(new { success = true, message = "Reaction sent" });
		}
		catch (Exception ex)
		{
			return StatusCode(500, $"Error sending reaction: {ex.Message}");
		}
	}

	[HttpGet("reactions/latest")] 
	public async Task<ActionResult<IEnumerable<Reaction>>> GetLatestReactions([FromQuery] string identityUserId, [FromQuery] int limit = 20, [FromQuery] int skip = 0)
	{
		if (!_context.IsConnected)
		{
			return StatusCode(503, "Service unavailable - database connection failed");
		}
		try
		{
			var reactions = await _context.Reactions!
				.Find(r => r.ToIdentityUserId == identityUserId)
				.SortByDescending(r => r.CreatedAt)
				.Skip(skip)
				.Limit(limit)
				.ToListAsync();
			return Ok(reactions);
		}
		catch (Exception ex)
		{
			return StatusCode(500, $"Error fetching reactions: {ex.Message}");
		}
	}
}


