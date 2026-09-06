using System.Text.Json.Serialization;

namespace LinkdUnified.Models;

public class ConnectAccountResponse
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "connected";

    [JsonPropertyName("connectedAt")]
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("profile")]
    public ConnectedProfileInfo? Profile { get; set; }
}

public class ConnectedProfileInfo
{
    [JsonPropertyName("memberUrn")]
    public string? MemberUrn { get; set; }

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("headline")]
    public string? Headline { get; set; }

    [JsonPropertyName("publicIdentifier")]
    public string? PublicIdentifier { get; set; }
}
