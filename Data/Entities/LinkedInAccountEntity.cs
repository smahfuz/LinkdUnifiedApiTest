using System.ComponentModel.DataAnnotations;

namespace LinkdUnified.Data.Entities;

public class LinkedInAccountEntity
{
    [Key]
    public string AccountId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string LiAtCookie { get; set; } = string.Empty;

    public string JsessionId { get; set; } = string.Empty;

    public string? MemberUrn { get; set; }

    public string? FullName { get; set; }

    public string? Headline { get; set; }

    public string? PublicIdentifier { get; set; }

    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastSyncedAt { get; set; }

    public bool IsActive { get; set; } = true;

    public List<ConversationEntity> Conversations { get; set; } = new();

    public List<WebhookSubscriptionEntity> Webhooks { get; set; } = new();
}
