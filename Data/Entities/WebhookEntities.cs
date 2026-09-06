using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkdUnified.Data.Entities;

public class WebhookSubscriptionEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string? AccountId { get; set; }

    [Required]
    public string TargetUrl { get; set; } = string.Empty;

    public string? Secret { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(AccountId))]
    public LinkedInAccountEntity? Account { get; set; }

    public List<WebhookEventLogEntity> EventLogs { get; set; } = new();
}

public class WebhookEventLogEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    public string WebhookSubscriptionId { get; set; } = string.Empty;

    public string EventType { get; set; } = "message.received";

    public string Payload { get; set; } = string.Empty;

    public int? StatusCode { get; set; }

    public bool IsSuccess { get; set; }

    public string? ResponseBody { get; set; }

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(WebhookSubscriptionId))]
    public WebhookSubscriptionEntity? Subscription { get; set; }
}
