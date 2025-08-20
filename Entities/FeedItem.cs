using MongoDB.Bson.Serialization.Attributes;

namespace User.Entities;

public class FeedItem : BaseEntity
{
	[BsonElement("identityUserId")]
	public string IdentityUserId { get; set; } = string.Empty;

	[BsonElement("type")]
	public string Type { get; set; } = string.Empty; // recent_song | top_songs_week | top_artists_week | common_artists

	// For recent song
	[BsonElement("songId")]
	public string? SongId { get; set; }

	[BsonElement("songTitle")]
	public string? SongTitle { get; set; }

	[BsonElement("artist")]
	public string? Artist { get; set; }

	[BsonElement("coverUrl")]
	public string? CoverUrl { get; set; }

	[BsonElement("playedAt")]
	public DateTime? PlayedAt { get; set; }

	[BsonElement("key")]
	public string? Key { get; set; } // stable de-dupe key per identityUserId+type+songId or payload

	// For weekly top
	[BsonElement("topArtists")]
	public List<string>? TopArtists { get; set; }

	[BsonElement("topSongs")]
	public List<string>? TopSongs { get; set; }

	// For common artists
	[BsonElement("withIdentityUserId")]
	public string? WithIdentityUserId { get; set; }

	[BsonElement("commonArtists")]
	public List<string>? CommonArtists { get; set; }
}


