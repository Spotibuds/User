using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Models;
using User.Services;

namespace User.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FeedController : ControllerBase
{
	private readonly MongoDbContext _context;
	private readonly INowPlayingStore _nowPlaying;

	public FeedController(MongoDbContext context, INowPlayingStore nowPlaying)
	{
		_context = context;
		_nowPlaying = nowPlaying;
	}

	[HttpGet("slides")]
	public async Task<ActionResult<object>> GetSlides([FromQuery] string identityUserId, [FromQuery] int limit = 20, [FromQuery] int skip = 0)
	{
		Console.WriteLine($"[Feed API] GetSlides called with identityUserId: {identityUserId}, limit: {limit}, skip: {skip}");
		
		if (!_context.IsConnected || _context.Users == null)
		{
			Console.WriteLine("[Feed API] Database connection failed");
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
				Console.WriteLine($"[Feed API] User not found for identityUserId: {identityUserId}");
				return NotFound("User not found");
			}
			
			Console.WriteLine($"[Feed API] Found current user: {currentUser.UserName}");

			// Get other users who are not private and not the current user
			var otherUsers = await _context.Users
				.Find(u => !u.IsPrivate && u.IdentityUserId != identityUserId)
				.Project<Models.User>(Builders<Models.User>.Projection
					.Include(u => u.IdentityUserId)
					.Include(u => u.UserName)
					.Include(u => u.DisplayName)
					.Include(u => u.ListeningHistory)
					.Include(u => u.TopArtistsCurrentWeek)
				)
				.ToListAsync();

			Console.WriteLine($"[Feed API] Found {otherUsers.Count} other users");

			if (!otherUsers.Any())
			{
				Console.WriteLine("[Feed API] No other users found, returning empty list");
				return Ok(new List<object>());
			}

			// Shuffle users for randomness
			var rng = new Random();
			otherUsers = otherUsers.OrderBy(_ => rng.Next()).ToList();

			var slides = new List<object>();
			var weekStart = DateTime.UtcNow.AddDays(-7);
			DateTime WeekStartUtc()
			{
				var now = DateTime.UtcNow;
				int diff = (int)now.DayOfWeek; // Sunday=0
				return new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-diff);
			}
			string WeekKey() => WeekStartUtc().ToString("yyyyMMdd");

			// 0) Persisted recent-song posts: fetch a larger window, then we'll mix with weekly slides
			var publicIds = new HashSet<string>(otherUsers.Select(u => u.IdentityUserId));
			var recentPersisted = _context.Feed != null
				? await _context.Feed
					.Find(f => f.Type == "recent_song" && publicIds.Contains(f.IdentityUserId))
					.SortByDescending(f => f.PlayedAt)
					.Limit(Math.Max(limit * 5, 50))
					.ToListAsync()
				: new List<FeedItem>();

			// Map persisted items to slides (include stable postId)
			foreach (var item in recentPersisted)
			{
				var owner = otherUsers.FirstOrDefault(u => u.IdentityUserId == item.IdentityUserId);
				if (owner == null) continue;
				slides.Add(new {
					type = "recent_song",
					postId = item.Id,
					identityUserId = item.IdentityUserId,
					username = owner.UserName,
					displayName = owner.DisplayName,
					songId = item.SongId,
					songTitle = item.SongTitle,
					artist = item.Artist,
					coverUrl = item.CoverUrl,
					playedAt = item.PlayedAt
				});
			}

			// 1..3) Weekly slides derived from user data for variety
			foreach (var user in otherUsers.Take(50)) // Limit to prevent too many slides
			{
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
							postId = $"weekly:artists:{user.IdentityUserId}:{WeekKey()}",
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
							postId = $"weekly:songs:{user.IdentityUserId}:{WeekKey()}",
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
							postId = $"common:{identityUserId}:{user.IdentityUserId}:{WeekKey()}",
							identityUserId = user.IdentityUserId,
							username = user.UserName,
							displayName = user.DisplayName,
							withIdentityUserId = identityUserId,
							commonArtists = common
						});
					}
				}
			}

			// Optionally inject ephemeral Now Playing cards (not persisted)
			var ttlSeconds = 90;
			var nowPlayingCards = new List<object>();
			foreach (var uid in publicIds.Take(80))
			{
				var np = _nowPlaying.Get(uid);
				if (np != null && np.IsPlaying && (DateTime.UtcNow - np.UpdatedAt).TotalSeconds <= ttlSeconds)
				{
					nowPlayingCards.Add(new {
						type = "now_playing",
						identityUserId = np.IdentityUserId,
						songId = np.SongId,
						songTitle = np.SongTitle,
						artist = np.Artist,
						coverUrl = np.CoverUrl,
						positionSec = np.PositionSec,
						updatedAt = np.UpdatedAt
					});
				}
			}
			// Lightly mix-in now playing items at the head for freshness
			slides = nowPlayingCards.Concat(slides).ToList();

			// Shuffle the slides and paginate
			slides = slides.OrderBy(_ => rng.Next()).Skip(skip).Take(limit).ToList();
			Console.WriteLine($"[Feed API] Returning {slides.Count} slides after shuffle and pagination");
			return Ok(slides);
		}
		catch (Exception ex)
		{
			return StatusCode(500, $"Error generating slides: {ex.Message}");
		}
	}

	[HttpPost("nowplaying")]
	public ActionResult<object> SetNowPlaying([FromBody] NowPlayingState state, [FromQuery] int ttlSec = 90)
	{
		try
		{
			var clamped = Math.Max(30, Math.Min(ttlSec, 180));
			_nowPlaying.Set(state, TimeSpan.FromSeconds(clamped));
			return Ok(new { success = true });
		}
		catch (Exception ex)
		{
			return StatusCode(500, $"Error setting now playing: {ex.Message}");
		}
	}

	[HttpDelete("nowplaying/{identityUserId}")]
	public ActionResult<object> ClearNowPlaying(string identityUserId)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(identityUserId))
			{
				return BadRequest("Identity user ID is required");
			}

			_nowPlaying.Clear(identityUserId);
			return Ok(new { success = true });
		}
		catch (Exception ex)
		{
			return StatusCode(500, $"Error clearing now playing: {ex.Message}");
		}
	}

	public class NowPlayingQuery
	{
		public List<string> UserIds { get; set; } = new();
	}

	[HttpPost("nowplaying/batch")] // fetch many in one call
	public ActionResult<IEnumerable<NowPlayingState>> GetNowPlayingBatch([FromBody] NowPlayingQuery query)
	{
		try
		{
			var list = _nowPlaying.GetMany(query.UserIds).ToList();
			return Ok(list);
		}
		catch (Exception ex)
		{
			return StatusCode(500, $"Error fetching now playing: {ex.Message}");
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
			// Validate emoji - only allow the 5 predefined reactions
			var validEmojis = new[] { "👍", "❤️", "😂", "😮", "🔥", "👏" };
			if (string.IsNullOrEmpty(request.Emoji) || !validEmojis.Contains(request.Emoji))
			{
				return BadRequest(new { success = false, message = "Invalid emoji. Only predefined reactions are allowed." });
			}

			request.CreatedAt = DateTime.UtcNow;
			DateTime WeekStartUtc()
			{
				var now = DateTime.UtcNow;
				int diff = (int)now.DayOfWeek; // Sunday=0
				return new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-diff);
			}
			string WeekKey() => WeekStartUtc().ToString("yyyyMMdd");

			// Attempt to derive postId when missing
			if (string.IsNullOrEmpty(request.PostId))
			{
				if (request.ContextType == "recent_song" && !string.IsNullOrEmpty(request.ToIdentityUserId) && !string.IsNullOrEmpty(request.SongId))
				{
					var recent = await _context.Feed!
						.Find(f => f.IdentityUserId == request.ToIdentityUserId && f.Type == "recent_song" && f.SongId == request.SongId)
						.SortByDescending(f => f.PlayedAt)
						.FirstOrDefaultAsync();
					if (recent != null) request.PostId = recent.Id;
				}
				else if (request.ContextType == "top_artists_week" && !string.IsNullOrEmpty(request.ToIdentityUserId))
				{
					request.PostId = $"weekly:artists:{request.ToIdentityUserId}:{WeekKey()}";
				}
				else if (request.ContextType == "top_songs_week" && !string.IsNullOrEmpty(request.ToIdentityUserId))
				{
					request.PostId = $"weekly:songs:{request.ToIdentityUserId}:{WeekKey()}";
				}
				else if (request.ContextType == "common_artists" && !string.IsNullOrEmpty(request.ToIdentityUserId) && !string.IsNullOrEmpty(request.FromIdentityUserId))
				{
					request.PostId = $"common:{request.FromIdentityUserId}:{request.ToIdentityUserId}:{WeekKey()}";
				}
			}

			// Special handling for now_playing reactions - redirect to recent_song post
			if (request.ContextType == "now_playing" && !string.IsNullOrEmpty(request.ToIdentityUserId) && !string.IsNullOrEmpty(request.SongId))
			{
				Console.WriteLine($"[Reaction] Processing now_playing reaction: User={request.ToIdentityUserId}, Song={request.SongId}, Emoji={request.Emoji}");
				
				// Check if Feed collection is available
				if (_context.Feed == null)
				{
					Console.WriteLine($"[Reaction ERROR] Feed collection is null! Cannot redirect now_playing reaction.");
					return BadRequest(new { success = false, message = "Feed service unavailable" });
				}
				
				// Look for a recent_song post for the same user and song
				var recentSongPost = await _context.Feed
					.Find(f => f.IdentityUserId == request.ToIdentityUserId && f.Type == "recent_song" && f.SongId == request.SongId)
					.SortByDescending(f => f.PlayedAt)
					.FirstOrDefaultAsync();
				
				Console.WriteLine($"[Reaction] Database query completed. Found recent_song post: {recentSongPost != null}");
				
				if (recentSongPost != null)
				{
					// Redirect the reaction to the recent_song post
					request.PostId = recentSongPost.Id;
					request.ContextType = "recent_song";
					Console.WriteLine($"[Reaction] SUCCESS: Redirecting now_playing reaction to recent_song post: {recentSongPost.Id} (PlayedAt: {recentSongPost.PlayedAt})");
				}
				else
				{
					// Debug: Let's see what recent_song posts exist for this user
					var allRecentSongs = await _context.Feed
						.Find(f => f.IdentityUserId == request.ToIdentityUserId && f.Type == "recent_song")
						.SortByDescending(f => f.PlayedAt)
						.Limit(5)
						.ToListAsync();
					Console.WriteLine($"[Reaction DEBUG] User {request.ToIdentityUserId} has {allRecentSongs.Count} recent_song posts:");
					foreach (var song in allRecentSongs)
					{
						Console.WriteLine($"  - SongId: {song.SongId}, Title: {song.SongTitle}, PlayedAt: {song.PlayedAt}");
					}
					
					// Try different approaches to find a matching recent_song post
					FeedItem? targetPost = null;
					
					// 1. Try exact songId match (case-insensitive)
					targetPost = allRecentSongs.FirstOrDefault(s => 
						string.Equals(s.SongId, request.SongId, StringComparison.OrdinalIgnoreCase));
					
					if (targetPost == null)
					{
						// 2. Try matching by song title (case-insensitive)
						targetPost = allRecentSongs.FirstOrDefault(s => 
							!string.IsNullOrEmpty(s.SongTitle) && !string.IsNullOrEmpty(request.SongTitle) &&
							string.Equals(s.SongTitle.Trim(), request.SongTitle.Trim(), StringComparison.OrdinalIgnoreCase));
					}
					
					if (targetPost == null)
					{
						// 3. As last resort, use the most recent song by this user
						targetPost = allRecentSongs.FirstOrDefault();
					}
					
					if (targetPost != null)
					{
						Console.WriteLine($"[Reaction] Redirecting to recent_song post: {targetPost.Id} (SongId: {targetPost.SongId}, Title: {targetPost.SongTitle})");
						request.PostId = targetPost.Id;
						request.ContextType = "recent_song";
					}
					else
					{
						// This user has never listened to any songs - we can't create a meaningful reaction
						Console.WriteLine($"[Reaction ERROR] User {request.ToIdentityUserId} has no listening history. Cannot process now_playing reaction.");
						return BadRequest(new { success = false, message = "Cannot react to now playing - user has no listening history." });
					}
				}
			}

			// Check if reaction already exists (for toggle behavior)
			var existingReaction = await _context.Reactions!
				.Find(r => r.PostId == request.PostId && 
						  r.FromIdentityUserId == request.FromIdentityUserId && 
						  r.Emoji == request.Emoji)
				.FirstOrDefaultAsync();

			if (existingReaction != null)
			{
				// Remove existing reaction (toggle off)
				await _context.Reactions!.DeleteOneAsync(r => r.Id == existingReaction.Id);
				return Ok(new { success = true, message = "Reaction removed", action = "removed" });
			}
			else
			{
				// Add new reaction (toggle on)
				Console.WriteLine($"[Reaction] Adding new reaction: PostId={request.PostId}, ContextType={request.ContextType}, Emoji={request.Emoji}, From={request.FromIdentityUserId}, To={request.ToIdentityUserId}");
				await _context.Reactions!.InsertOneAsync(request);
				Console.WriteLine($"[Reaction] SUCCESS: Reaction added with ID: {request.Id}");
				return Ok(new { success = true, message = "Reaction sent", action = "added" });
			}
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

	[HttpGet("reactions/by-post")]
	public async Task<ActionResult<IEnumerable<Reaction>>> GetReactionsByPost([FromQuery] string postId, [FromQuery] string? currentUserId = null)
	{
		if (!_context.IsConnected)
		{
			return StatusCode(503, "Service unavailable - database connection failed");
		}
		try
		{
			var reactions = await _context.Reactions!
				.Find(r => r.PostId == postId)
				.SortByDescending(r => r.CreatedAt)
				.ToListAsync();

			// If currentUserId is provided, filter reactions to show only those from friends
			if (!string.IsNullOrEmpty(currentUserId))
			{
				// Get current user
				var currentUser = await _context.Users
					.Find(u => u.IdentityUserId == currentUserId)
					.FirstOrDefaultAsync();

				if (currentUser != null)
				{
					// Get friends of the current user
					var friendships = await _context.Friends!
						.Find(f => (f.UserId == currentUser.Id || f.FriendId == currentUser.Id) && f.Status == FriendStatus.Accepted)
						.ToListAsync();

					var friendIds = new HashSet<string>();
					foreach (var friendship in friendships)
					{
						if (friendship.UserId == currentUser.Id)
						{
							// Get the friend's identity user ID
							var friend = await _context.Users.Find(u => u.Id == friendship.FriendId).FirstOrDefaultAsync();
							if (friend != null) friendIds.Add(friend.IdentityUserId);
						}
						else
						{
							// Get the user's identity user ID  
							var friend = await _context.Users.Find(u => u.Id == friendship.UserId).FirstOrDefaultAsync();
							if (friend != null) friendIds.Add(friend.IdentityUserId);
						}
					}

					// Also include the current user's own reactions
					friendIds.Add(currentUserId);

					// Filter reactions to only include those from friends (and self)
					reactions = reactions.Where(r => friendIds.Contains(r.FromIdentityUserId)).ToList();
				}
			}

			return Ok(reactions);
		}
		catch (Exception ex)
		{
			return StatusCode(500, $"Error fetching reactions: {ex.Message}");
		}
	}

	[HttpGet("post")]
	public async Task<ActionResult<object>> GetPost([FromQuery] string id)
	{
		if (!_context.IsConnected || _context.Users == null)
		{
			return StatusCode(503, "Service unavailable - database connection failed");
		}
		try
		{
			// URL decode the id first
			var decodedId = Uri.UnescapeDataString(id);
			
			// 1) computed slides by synthetic id (check these first since they contain colons)
			if (decodedId.StartsWith("weekly:artists:") || decodedId.StartsWith("weekly:songs:") || decodedId.StartsWith("common:"))
			{
				var parts = decodedId.Split(':');
				if (decodedId.StartsWith("weekly:artists:") && parts.Length == 4)
				{
					var identityUserId = parts[2];
					var weekKey = parts[3];
					var user = await _context.Users.Find(u => u.IdentityUserId == identityUserId).FirstOrDefaultAsync();
					if (user == null) return NotFound();
					var weekStart = DateTime.SpecifyKind(DateTime.ParseExact(weekKey, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
					var weekItems = user.ListeningHistory.Where(h => h.PlayedAt >= weekStart).ToList();
					var dict = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
					foreach (var it in weekItems)
					{
						if (string.IsNullOrWhiteSpace(it.Artist)) continue;
						foreach (var a in it.Artist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
						{
							var k = a.Trim(); if (k.Length==0) continue; if(!dict.ContainsKey(k)) dict[k]=0; dict[k]++;
						}
					}
					var topArtists = dict.Select(kv => new { name = kv.Key, count = kv.Value }).OrderByDescending(x => x.count).ThenBy(x => x.name).Take(3).ToList();
					return Ok(new { type = "top_artists_week", postId = decodedId, identityUserId = identityUserId, username = user.UserName, displayName = user.DisplayName, topArtists });
				}
				if (decodedId.StartsWith("weekly:songs:") && parts.Length == 4)
				{
					var identityUserId = parts[2];
					var weekKey = parts[3];
					var user = await _context.Users.Find(u => u.IdentityUserId == identityUserId).FirstOrDefaultAsync();
					if (user == null) return NotFound();
					var weekStart = DateTime.SpecifyKind(DateTime.ParseExact(weekKey, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
					var weekItems = user.ListeningHistory.Where(h => h.PlayedAt >= weekStart).ToList();
					var songMap = new Dictionary<string,(string? SongId,string? SongTitle,string? Artist,int Count)>(StringComparer.OrdinalIgnoreCase);
					foreach (var it in weekItems)
					{
						var key = !string.IsNullOrWhiteSpace(it.SongId) ? it.SongId! : (it.SongTitle ?? "unknown");
						if (!songMap.ContainsKey(key)) songMap[key] = (it.SongId, it.SongTitle, it.Artist, 0);
						songMap[key] = (songMap[key].SongId, songMap[key].SongTitle, songMap[key].Artist, songMap[key].Count+1);
					}
					var topSongs = songMap.Select(kv => new { songId = kv.Value.SongId, songTitle = kv.Value.SongTitle, artist = kv.Value.Artist, count = kv.Value.Count }).OrderByDescending(s=>s.count).ThenBy(s=>s.songTitle).Take(3).ToList();
					return Ok(new { type = "top_songs_week", postId = decodedId, identityUserId = identityUserId, username = user.UserName, displayName = user.DisplayName, topSongs });
				}
				if (decodedId.StartsWith("common:") && parts.Length == 4)
				{
					var viewerId = parts[1];
					var authorId = parts[2];
					var weekKey = parts[3];
					var viewer = await _context.Users.Find(u => u.IdentityUserId == viewerId).FirstOrDefaultAsync();
					var author = await _context.Users.Find(u => u.IdentityUserId == authorId).FirstOrDefaultAsync();
					if (viewer == null || author == null) return NotFound();
					var weekStart = DateTime.SpecifyKind(DateTime.ParseExact(weekKey, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
					var viewerArtists = new HashSet<string>(viewer.ListeningHistory.Where(h=>h.PlayedAt>=weekStart).SelectMany(h => (h.Artist ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)).Select(a=>a.Trim()), StringComparer.OrdinalIgnoreCase);
					var authorArtists = new HashSet<string>(author.ListeningHistory.Where(h=>h.PlayedAt>=weekStart).SelectMany(h => (h.Artist ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)).Select(a=>a.Trim()), StringComparer.OrdinalIgnoreCase);
					var common = viewerArtists.Intersect(authorArtists, StringComparer.OrdinalIgnoreCase).Take(5).ToList();
					return Ok(new { type = "common_artists", postId = decodedId, identityUserId = authorId, username = author.UserName, displayName = author.DisplayName, withIdentityUserId = viewerId, commonArtists = common });
				}
			}

			// 2) recent_song persisted feed item by MongoId (check after synthetic IDs)
			if (_context.Feed != null && decodedId.Length >= 12 && !decodedId.Contains(":"))
			{
				var item = await _context.Feed.Find(f => f.Id == decodedId).FirstOrDefaultAsync();
				if (item != null)
				{
					var owner = await _context.Users.Find(u => u.IdentityUserId == item.IdentityUserId).FirstOrDefaultAsync();
					return Ok(new {
						type = "recent_song",
						postId = item.Id,
						identityUserId = item.IdentityUserId,
						username = owner?.UserName,
						displayName = owner?.DisplayName,
						songId = item.SongId,
						songTitle = item.SongTitle,
						artist = item.Artist,
						coverUrl = item.CoverUrl,
						playedAt = item.PlayedAt
					});
				}
			}

			return NotFound("Post not found");
		}
		catch (Exception ex)
		{
			return StatusCode(500, $"Error fetching post: {ex.Message}");
		}
	}
}


