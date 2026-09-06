using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkdUnified.Data.Entities;

public class ConversationEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string AccountId { get; set; } = string.Empty;

    public string EntityUrn { get; set; } = string.Empty;

    public string? Title { get; set; }

    public int UnreadCount { get; set; }

    public DateTime? LastActivityAt { get; set; }

    public string? ParticipantsJson { get; set; }

    public string? LastMessageJson { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(AccountId))]
    public LinkedInAccountEntity? Account { get; set; }

    public List<MessageEntity> Messages { get; set; } = new();
}
