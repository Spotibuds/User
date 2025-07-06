using System.ComponentModel.DataAnnotations;

namespace User.Entities;

public class Follow : BaseEntity
{
    [Required]
    public Guid FollowerId { get; set; }

    [Required]
    public Guid FollowedId { get; set; }
} 