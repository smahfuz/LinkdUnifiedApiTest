using System.Net;

namespace LinkdUnified.Models;

public class LinkedInSessionState
{
    public string AccountId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string LiAtCookie { get; set; } = string.Empty;
    public string JsessionId { get; set; } = string.Empty;
    public string? MemberUrn { get; set; }
    public ConnectedProfileInfo? Profile { get; set; }
    public CookieContainer CookieContainer { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Gets the formatted CSRF token suitable for the 'csrf-token' header (quotes removed).
    /// </summary>
    public string GetCsrfToken()
    {
        return JsessionId.Trim('"');
    }
}
