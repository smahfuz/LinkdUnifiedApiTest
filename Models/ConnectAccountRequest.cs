using System.Text.Json.Serialization;

namespace LinkdUnified.Models;

public class ConnectAccountRequest
{
    private string? _username;

    /// <summary>
    /// LinkedIn account email or username.
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username
    {
        get => _username;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _username = value;
            }
        }
    }

    /// <summary>
    /// Alias for Username (supports either 'email' or 'username' JSON field).
    /// </summary>
    [JsonPropertyName("email")]
    public string? Email
    {
        get => _username;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _username = value;
            }
        }
    }

    /// <summary>
    /// LinkedIn account password.
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>
    /// Optional direct session cookie (li_at) for connecting with an existing active session.
    /// </summary>
    [JsonPropertyName("sessionCookie")]
    public string? SessionCookie { get; set; }

    private string? _jsessionId;

    /// <summary>
    /// Optional JSESSIONID / CSRF token from LinkedIn cookies (e.g. ajax:1234567890123456789).
    /// </summary>
    [JsonPropertyName("jsessionId")]
    public string? JsessionId
    {
        get => _jsessionId;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, "string", StringComparison.OrdinalIgnoreCase))
            {
                _jsessionId = value;
            }
        }
    }

    /// <summary>
    /// Alias for jsessionId.
    /// </summary>
    [JsonPropertyName("csrfToken")]
    public string? CsrfToken
    {
        get => _jsessionId;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, "string", StringComparison.OrdinalIgnoreCase))
            {
                _jsessionId = value;
            }
        }
    }
}
