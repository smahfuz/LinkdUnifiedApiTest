using System.Net;
using System.Text.Json.Serialization;

namespace LinkdUnified.Models;

public class PendingChallengeState
{
    public string ChallengeToken { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ChallengeUrl { get; set; } = string.Empty;
    public string SubmitUrl { get; set; } = string.Empty;
    public string? ResendUrl { get; set; }
    public string ChallengeType { get; set; } = "EMAIL_CODE";
    public Dictionary<string, string> FormFields { get; set; } = new();
    public CookieContainer CookieContainer { get; set; } = new();
    public object? BrowserPage { get; set; }
    public object? BrowserInstance { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SubmitChallengeRequest
{
    /// <summary>
    /// The challenge token returned from the initial connect request.
    /// </summary>
    [JsonPropertyName("challengeToken")]
    public string ChallengeToken { get; set; } = string.Empty;

    /// <summary>
    /// The OTP / PIN verification code received via email, SMS, or authenticator app.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Alias for Code (supports 'pin' or 'code').
    /// </summary>
    [JsonPropertyName("pin")]
    public string? Pin
    {
        get => Code;
        set => Code = value ?? string.Empty;
    }
}

public class ResendChallengeRequest
{
    /// <summary>
    /// The challenge token returned from the initial connect request.
    /// </summary>
    [JsonPropertyName("challengeToken")]
    public string ChallengeToken { get; set; } = string.Empty;
}

public class ConnectAccountResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "connected";

    [JsonPropertyName("requiresChallenge")]
    public bool RequiresChallenge { get; set; }

    [JsonPropertyName("challengeToken")]
    public string? ChallengeToken { get; set; }

    [JsonPropertyName("challengeType")]
    public string? ChallengeType { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("account")]
    public ConnectAccountResponse? Account { get; set; }
}
