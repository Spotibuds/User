namespace User.Entities;
public class HistoryEvent : BaseEntity
{
    public string IdentityUserId { get; set; } = "";
    public string SongId { get; set; } = "";
    public string SongTitle { get; set; } = "";
    public string Artist { get; set; } = "";
    // Preserve actual catalogue names, including commas. Older rows retain the display-string fallback.
    public List<string>? ArtistNames { get; set; }
    public string? CoverUrl { get; set; }
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;
    public int Duration { get; set; }
}
public class FollowEdge : BaseEntity
{
    public string FollowerId { get; set; } = "";
    public string FollowedId { get; set; } = "";
}
