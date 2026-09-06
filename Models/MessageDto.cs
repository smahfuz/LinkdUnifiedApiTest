using System.Text.Json.Serialization;

namespace LinkdUnified.Models;

public class MessageListResponse
{
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("messages")]
    public List<MessageDto> Messages { get; set; } = new();
}

public class MessageDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

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
}
