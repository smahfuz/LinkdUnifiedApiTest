using System.Text.Json.Serialization;

namespace LinkdUnified.Models;

public class CreateWebhookRequest
{
    [JsonPropertyName("targetUrl")]
    public string TargetUrl { get; set; } = string.Empty;

    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    [JsonPropertyName("accountId")]
    public string? AccountId { get; set; }
}

public class WebhookSubscriptionDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("targetUrl")]
    public string TargetUrl { get; set; } = string.Empty;

    [JsonPropertyName("accountId")]
    public string? AccountId { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

public class WebhookMessageEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "message.received";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

    [JsonPropertyName("conversationTitle")]
    public string? ConversationTitle { get; set; }

    [JsonPropertyName("senderName")]
    public string? SenderName { get; set; }

    [JsonPropertyName("senderUrn")]
    public string? SenderUrn { get; set; }

    [JsonPropertyName("isFromMe")]
    public bool IsFromMe { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("sentAt")]
    public DateTime? SentAt { get; set; }

    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;
}
