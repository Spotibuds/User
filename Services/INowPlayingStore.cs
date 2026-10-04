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
	void Clear(string identityUserId);
	long Reserve(string identityUserId);
	bool TrySet(NowPlayingState state, TimeSpan ttl, long reservation);
	bool TryClear(string identityUserId, long reservation);
	long SnapshotVersion();
	NowPlayingState? GetAt(string identityUserId, long maxPublicationVersion);
	string InstanceId { get; }
}

public class NowPlayingStore : INowPlayingStore
{
	private readonly IMemoryCache _cache;
	private readonly TimeProvider _clock;
	private readonly object _gate = new();
	private long _sequence;
	public string InstanceId { get; } = Guid.NewGuid().ToString("N");
	private sealed record Slot(long Version, long PublicationVersion, NowPlayingState? State, DateTimeOffset Expires);
	public NowPlayingStore(IMemoryCache cache, TimeProvider clock) { _cache = cache; _clock = clock; }

	private static string KeyOf(string identityUserId) => $"nowplaying:{identityUserId}";

	public void Set(NowPlayingState state, TimeSpan ttl)
	{
		TrySet(state, ttl, Reserve(state.IdentityUserId));
	}
	public long Reserve(string identityUserId)
	{
		lock (_gate)
		{
			_cache.TryGetValue(KeyOf(identityUserId), out Slot? old);
			var version = ++_sequence;
			Store(identityUserId, new Slot(version, old?.PublicationVersion ?? 0, old?.State, old?.Expires ?? DateTimeOffset.MinValue));
			return version;
		}
	}
	public bool TrySet(NowPlayingState state, TimeSpan ttl, long reservation)
	{
		lock (_gate)
		{
			if (!_cache.TryGetValue(KeyOf(state.IdentityUserId), out Slot? slot) || slot?.Version != reservation) return false;
			var copy = Copy(state); copy.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
			Store(state.IdentityUserId, new Slot(reservation, ++_sequence, copy, _clock.GetUtcNow().Add(ttl))); return true;
		}
	}
	public bool TryClear(string identityUserId, long reservation)
	{
		lock (_gate) { if (!_cache.TryGetValue(KeyOf(identityUserId), out Slot? slot) || slot?.Version != reservation) return false; Store(identityUserId, new Slot(reservation, ++_sequence, null, DateTimeOffset.MinValue)); return true; }
	}
	private void Store(string account, Slot slot) => _cache.Set(KeyOf(account), slot, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10), Size = 1 });
	private static NowPlayingState Copy(NowPlayingState s) => new() { IdentityUserId = s.IdentityUserId, SongId = s.SongId, SongTitle = s.SongTitle, Artist = s.Artist, CoverUrl = s.CoverUrl, PositionSec = s.PositionSec, IsPlaying = s.IsPlaying, UpdatedAt = s.UpdatedAt };

	public long SnapshotVersion() { lock (_gate) return _sequence; }
	public NowPlayingState? Get(string identityUserId) => GetAt(identityUserId, long.MaxValue);
	public NowPlayingState? GetAt(string identityUserId, long maxPublicationVersion)
	{
		if (string.IsNullOrWhiteSpace(identityUserId)) return null;
		lock (_gate) { _cache.TryGetValue(KeyOf(identityUserId), out Slot? value); return value?.State != null && value.PublicationVersion <= maxPublicationVersion && value.Expires > _clock.GetUtcNow() ? Copy(value.State) : null; }
	}

	public IEnumerable<NowPlayingState> GetMany(IEnumerable<string> identityUserIds)
	{
		foreach (var id in identityUserIds.Distinct().Where(x => !string.IsNullOrWhiteSpace(x)))
		{
			var s = Get(id);
			if (s != null) yield return s;
		}
	}

	public void Clear(string identityUserId)
	{
		if (string.IsNullOrWhiteSpace(identityUserId)) return;
		lock (_gate) { var reservation = ++_sequence; Store(identityUserId, new Slot(reservation, ++_sequence, null, DateTimeOffset.MinValue)); }
	}
}
