using Microsoft.Extensions.Caching.Memory;

namespace User.Services;

public class NowPlayingState
{
	public string IdentityUserId { get; set; } = string.Empty;
	public string SongId { get; set; } = string.Empty;
	public string? SongTitle { get; set; }
	public string? Artist { get; set; }
	public string? CoverUrl { get; set; }
	public int PositionSec { get; set; }
	public bool IsPlaying { get; set; }
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public interface INowPlayingStore
{
	void Set(NowPlayingState state, TimeSpan ttl);
	NowPlayingState? Get(string identityUserId);
	IEnumerable<NowPlayingState> GetMany(IEnumerable<string> identityUserIds);
}

public class NowPlayingStore : INowPlayingStore
{
	private readonly IMemoryCache _cache;
	public NowPlayingStore(IMemoryCache cache)
	{
		_cache = cache;
	}

	private static string KeyOf(string identityUserId) => $"nowplaying:{identityUserId}";

	public void Set(NowPlayingState state, TimeSpan ttl)
	{
		if (string.IsNullOrWhiteSpace(state.IdentityUserId)) return;
		state.UpdatedAt = DateTime.UtcNow;
		_cache.Set(KeyOf(state.IdentityUserId), state, new MemoryCacheEntryOptions
		{
			AbsoluteExpirationRelativeToNow = ttl
		});
	}

	public NowPlayingState? Get(string identityUserId)
	{
		if (string.IsNullOrWhiteSpace(identityUserId)) return null;
		_cache.TryGetValue(KeyOf(identityUserId), out NowPlayingState? value);
		return value;
	}

	public IEnumerable<NowPlayingState> GetMany(IEnumerable<string> identityUserIds)
	{
		foreach (var id in identityUserIds.Distinct().Where(x => !string.IsNullOrWhiteSpace(x)))
		{
			var s = Get(id);
			if (s != null) yield return s;
		}
	}
}


