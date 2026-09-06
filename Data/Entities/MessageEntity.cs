using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkdUnified.Data.Entities;

public class MessageEntity
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string ConversationId { get; set; } = string.Empty;

    public string? SenderName { get; set; }

    public string? SenderUrn { get; set; }

    public bool IsFromMe { get; set; }

    public string? Text { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ConversationId))]
    public ConversationEntity? Conversation { get; set; }
}
