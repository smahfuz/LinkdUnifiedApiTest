using System.Text.Json.Serialization;

namespace LinkdUnified.Models;

public class ConversationListResponse
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("conversations")]
    public List<ConversationDto> Conversations { get; set; } = new();
}

public class ConversationDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("entityUrn")]
    public string EntityUrn { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("unreadCount")]
    public int UnreadCount { get; set; }

    [JsonPropertyName("lastActivityAt")]
    public DateTime? LastActivityAt { get; set; }

    [JsonPropertyName("participants")]
    public List<ParticipantDto> Participants { get; set; } = new();

    [JsonPropertyName("lastMessage")]
    public MessagePreviewDto? LastMessage { get; set; }
}

public class ParticipantDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("profileUrn")]
    public string? ProfileUrn { get; set; }

    [JsonPropertyName("headline")]
    public string? Headline { get; set; }

    [JsonPropertyName("publicIdentifier")]
    public string? PublicIdentifier { get; set; }
}

public class MessagePreviewDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("senderName")]
    public string? SenderName { get; set; }

    [JsonPropertyName("senderUrn")]
    public string? SenderUrn { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("sentAt")]
    public DateTime? SentAt { get; set; }
}
