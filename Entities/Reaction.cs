using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace User.Entities;

public class Reaction : BaseEntity
{
	[BsonElement("toIdentityUserId")]
	public string ToIdentityUserId { get; set; } = string.Empty;

	[BsonElement("fromIdentityUserId")]
	public string FromIdentityUserId { get; set; } = string.Empty;

	[BsonElement("fromUserName")]
	public string? FromUserName { get; set; }

	[BsonElement("emoji")]
	public string Emoji { get; set; } = string.Empty;

	[BsonElement("postId")]
	public string? PostId { get; set; }

	// Optional context about what was reacted to
	[BsonElement("contextType")]
	public string? ContextType { get; set; } // e.g., recent_song | top_song | top_artist | common_artists

	[BsonElement("songId")]
	public string? SongId { get; set; }

	[BsonElement("songTitle")]
	public string? SongTitle { get; set; }

	[BsonElement("artist")]
	public string? Artist { get; set; }
}


