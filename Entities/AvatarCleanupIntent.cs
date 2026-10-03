namespace User.Entities;
public sealed class AvatarCleanupIntent : BaseEntity
{
    public string Url { get; set; } = "";
    public DateTime DueAt { get; set; }
    public int Attempts { get; set; }
}
