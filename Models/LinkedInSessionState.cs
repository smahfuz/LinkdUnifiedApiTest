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
    public string? RawCookies { get; set; }
    public List<CookieDto> BrowserCookies { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Persistent headless browser instance kept alive after login for API calls (Unipile-style).
    /// </summary>
    public object? PersistentBrowser { get; set; }

    /// <summary>
    /// Persistent browser page used for in-page fetch() API calls.
    /// </summary>
    public object? PersistentBrowserPage { get; set; }

    /// <summary>
    /// Last captured conversations JSON from network interception or DOM scraping.
    /// </summary>
    public string? LastInterceptedConversationsJson { get; set; }

    /// <summary>
    /// Gets the formatted CSRF token suitable for the 'csrf-token' header (quotes removed).
    /// </summary>
    public string GetCsrfToken()
    {
        return JsessionId.Trim('"');
    }
}

public class CookieDto
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? Path { get; set; }
    public double? Expires { get; set; }
    public bool? HttpOnly { get; set; }
    public bool? Secure { get; set; }
    public string? SameSite { get; set; }
}

