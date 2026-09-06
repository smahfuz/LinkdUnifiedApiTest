using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinkdUnified.Models;
using LinkdUnified.Services.Browser;

namespace LinkdUnified.Services;

public class LinkedInAuthService : ILinkedInAuthService
{
    private readonly ILinkedInSessionStore _sessionStore;
    private readonly ILinkedInBrowserAuthService _browserAuthService;
    private readonly ILogger<LinkedInAuthService> _logger;

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
    private const string LinkedInBaseUrl = "https://www.linkedin.com";
    private const string LoginUrl = "https://www.linkedin.com/login";
    private const string AuthenticateUrl = "https://www.linkedin.com/checkpoint/lg/login-submit";
    private const string VoyagerMeUrl = "https://www.linkedin.com/voyager/api/me";

    public LinkedInAuthService(
        ILinkedInSessionStore sessionStore,
        ILinkedInBrowserAuthService browserAuthService,
        ILogger<LinkedInAuthService> logger)
    {
        _sessionStore = sessionStore;
        _browserAuthService = browserAuthService;
        _logger = logger;
    }

    public async Task<ConnectAccountResult> ConnectAccountAsync(ConnectAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Check if direct session cookie (li_at) is provided
        if (!string.IsNullOrWhiteSpace(request.SessionCookie))
        {
            var directAccount = await ConnectWithSessionCookieAsync(request.SessionCookie.Trim(), request.JsessionId, cancellationToken);
            return new ConnectAccountResult
            {
                Status = "connected",
                RequiresChallenge = false,
                Account = directAccount,
                Message = "LinkedIn account connected successfully via session cookie."
            };
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ArgumentException("Either username & password or sessionCookie must be provided.");
        }

        // Unipile-style Headless Chrome Authentication (dispatches real 6-digit PIN email)
        _logger.LogInformation("Delegating login for {Username} to Headless Browser Auth Engine", request.Username);
        return await _browserAuthService.LoginWithBrowserAsync(request.Username.Trim(), request.Password, cancellationToken);
    }

    private async Task<ConnectAccountResult> ConnectWithCredentialsAsync(string username, string password, CancellationToken cancellationToken)
    {
        var cookieContainer = new CookieContainer();
        var handler = new SocketsHttpHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");

        _logger.LogInformation("Initiating LinkedIn authentication handshake for user {Username}", username);

        // 1. GET login page to retrieve CSRF and initial cookies
        var loginPageResponse = await client.GetAsync(LoginUrl, cancellationToken);
        var loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync(cancellationToken);

        // Extract hidden fields: loginCsrfParam, csrf_token, etc.
        var loginCsrfParam = ExtractInputValue(loginPageHtml, "loginCsrfParam") ?? ExtractInputValue(loginPageHtml, "csrf_token") ?? string.Empty;
        var sCPresent = ExtractInputValue(loginPageHtml, "s_c");

        var formData = new Dictionary<string, string>
        {
            { "session_key", username },
            { "session_password", password },
            { "isJsEnabled", "false" },
            { "loginCsrfParam", loginCsrfParam }
        };

        if (!string.IsNullOrEmpty(sCPresent))
        {
            formData["s_c"] = sCPresent;
        }

        // 2. Submit credentials
        var formContent = new FormUrlEncodedContent(formData);
        var postRequest = new HttpRequestMessage(HttpMethod.Post, AuthenticateUrl)
        {
            Content = formContent
        };
        postRequest.Headers.Referrer = new Uri(LoginUrl);
        postRequest.Headers.Add("Origin", LinkedInBaseUrl);

        var submitResponse = await client.SendAsync(postRequest, cancellationToken);
        var submitHtml = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        var finalUrl = submitResponse.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

        // Check all cookies in container
        var allCookies = cookieContainer.GetAllCookies();
        var liAtCookie = allCookies.FirstOrDefault(c => string.Equals(c.Name, "li_at", StringComparison.OrdinalIgnoreCase))?.Value;
        var jsessionId = allCookies.FirstOrDefault(c => string.Equals(c.Name, "JSESSIONID", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        // Check if 2FA or Checkpoint challenge was triggered
        if (string.IsNullOrEmpty(liAtCookie) && (finalUrl.Contains("checkpoint") || submitHtml.Contains("checkpoint") || submitHtml.Contains("challenge")))
        {
            _logger.LogInformation("LinkedIn checkpoint challenge triggered for {Username}. Final URL: {FinalUrl}", username, finalUrl);

            var formFields = ExtractAllHiddenInputs(submitHtml);
            var formAction = ExtractFormAction(submitHtml) ?? "/checkpoint/challenge/submit";
            if (!formAction.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                formAction = $"{LinkedInBaseUrl}{(formAction.StartsWith('/') ? "" : "/")}{formAction}";
            }

            var resendUrl = ExtractResendUrl(submitHtml);
            var (challengeType, instructions) = ExtractChallengeDetails(submitHtml);

            // If a resend/send email link is present, trigger it immediately so LinkedIn dispatches the email
            if (!string.IsNullOrEmpty(resendUrl))
            {
                try
                {
                    _logger.LogInformation("Triggering automatic email code dispatch for {Username} via {ResendUrl}", username, resendUrl);
                    var resendReq = new HttpRequestMessage(HttpMethod.Get, resendUrl);
                    resendReq.Headers.Referrer = new Uri(finalUrl);
                    var resendResp = await client.SendAsync(resendReq, cancellationToken);
                    var resendHtml = await resendResp.Content.ReadAsStringAsync(cancellationToken);
                    
                    // Update form fields if renewed on resend
                    var updatedFields = ExtractAllHiddenInputs(resendHtml);
                    if (updatedFields.Count > 0)
                    {
                        foreach (var kvp in updatedFields) formFields[kvp.Key] = kvp.Value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to automatically trigger resendUrl for {Username}", username);
                }
            }

            var challengeToken = Guid.NewGuid().ToString("N");
            var pendingState = new PendingChallengeState
            {
                ChallengeToken = challengeToken,
                Username = username,
                ChallengeUrl = finalUrl,
                SubmitUrl = formAction,
                ResendUrl = resendUrl,
                ChallengeType = challengeType,
                FormFields = formFields,
                CookieContainer = cookieContainer,
                CreatedAt = DateTime.UtcNow
            };

            _sessionStore.SavePendingChallenge(pendingState);

            return new ConnectAccountResult
            {
                Status = "checkpoint_required",
                RequiresChallenge = true,
                ChallengeToken = challengeToken,
                ChallengeType = challengeType,
                Message = instructions
            };
        }

        if (string.IsNullOrEmpty(liAtCookie))
        {
            if (finalUrl.Contains("login") || submitResponse.StatusCode == HttpStatusCode.Unauthorized || submitResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Invalid LinkedIn credentials for {Username}", username);
                throw new UnauthorizedAccessException("LinkedIn authentication failed: Invalid username or password.");
            }

            _logger.LogWarning("Authentication failed without specific error. Final URL: {FinalUrl}", finalUrl);
            throw new UnauthorizedAccessException("LinkedIn authentication failed. Please check credentials or try connecting with sessionCookie (li_at).");
        }

        // If JSESSIONID is missing, generate one
        if (string.IsNullOrEmpty(jsessionId))
        {
            jsessionId = $"\"ajax:{Guid.NewGuid():N}\"";
            cookieContainer.Add(new Uri(LinkedInBaseUrl), new Cookie("JSESSIONID", jsessionId, "/", ".linkedin.com"));
        }

        // 3. Fetch profile information to verify session and get account details
        var profileInfo = await FetchProfileInfoAsync(client, cookieContainer, jsessionId, cancellationToken);
        var accountId = profileInfo.MemberUrn ?? $"acc_{Guid.NewGuid():N}";

        var sessionState = new LinkedInSessionState
        {
            AccountId = accountId,
            Username = username,
            LiAtCookie = liAtCookie,
            JsessionId = jsessionId,
            MemberUrn = profileInfo.MemberUrn,
            Profile = profileInfo,
            CookieContainer = cookieContainer,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            IsActive = true
        };

        _sessionStore.SaveSession(sessionState);
        _logger.LogInformation("Successfully connected LinkedIn account {AccountId} for {Username}", accountId, username);

        var accountResponse = new ConnectAccountResponse
        {
            AccountId = accountId,
            Status = "connected",
            ConnectedAt = sessionState.CreatedAt,
            Profile = profileInfo
        };

        return new ConnectAccountResult
        {
            Status = "connected",
            RequiresChallenge = false,
            Account = accountResponse,
            Message = "LinkedIn account connected successfully."
        };
    }

    public async Task<ConnectAccountResponse> SubmitChallengeAsync(SubmitChallengeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ChallengeToken))
        {
            throw new ArgumentException("Challenge token is required.", nameof(request.ChallengeToken));
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new ArgumentException("Verification code/PIN is required.", nameof(request.Code));
        }

        var pending = _sessionStore.GetPendingChallenge(request.ChallengeToken);
        if (pending == null)
        {
            throw new ArgumentException("Challenge session not found or has expired. Please initiate connection again.");
        }

        // If challenge was created with Headless Browser, delegate to browser verification
        if (pending.BrowserPage != null)
        {
            _logger.LogInformation("Submitting PIN code to Headless Browser for challenge {ChallengeToken}", request.ChallengeToken);
            return await _browserAuthService.SubmitBrowserChallengeAsync(request.ChallengeToken, request.Code, cancellationToken);
        }

        var handler = new SocketsHttpHandler
        {
            CookieContainer = pending.CookieContainer,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");

        var formData = new Dictionary<string, string>(pending.FormFields)
        {
            { "pin", request.Code.Trim() },
            { "verificationCode", request.Code.Trim() }
        };

        var postRequest = new HttpRequestMessage(HttpMethod.Post, pending.SubmitUrl)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        postRequest.Headers.Referrer = new Uri(pending.ChallengeUrl);
        postRequest.Headers.Add("Origin", LinkedInBaseUrl);

        _logger.LogInformation("Submitting verification code for challenge {ChallengeToken} (User: {Username})", request.ChallengeToken, pending.Username);

        var response = await client.SendAsync(postRequest, cancellationToken);
        var responseHtml = await response.Content.ReadAsStringAsync(cancellationToken);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

        var allCookies = pending.CookieContainer.GetAllCookies();
        var liAtCookie = allCookies.FirstOrDefault(c => string.Equals(c.Name, "li_at", StringComparison.OrdinalIgnoreCase))?.Value;
        var jsessionId = allCookies.FirstOrDefault(c => string.Equals(c.Name, "JSESSIONID", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        if (string.IsNullOrEmpty(liAtCookie))
        {
            _logger.LogWarning("Failed checkpoint challenge for user {Username}. URL: {FinalUrl}", pending.Username, finalUrl);
            throw new UnauthorizedAccessException("Verification failed. The code entered may be incorrect or expired. Please check your email and try again.");
        }

        if (string.IsNullOrEmpty(jsessionId))
        {
            jsessionId = $"\"ajax:{Guid.NewGuid():N}\"";
            pending.CookieContainer.Add(new Uri(LinkedInBaseUrl), new Cookie("JSESSIONID", jsessionId, "/", ".linkedin.com"));
        }

        // Fetch profile
        var profileInfo = await FetchProfileInfoAsync(client, pending.CookieContainer, jsessionId, cancellationToken);
        var accountId = profileInfo.MemberUrn ?? $"acc_{Guid.NewGuid():N}";

        var sessionState = new LinkedInSessionState
        {
            AccountId = accountId,
            Username = pending.Username,
            LiAtCookie = liAtCookie,
            JsessionId = jsessionId,
            MemberUrn = profileInfo.MemberUrn,
            Profile = profileInfo,
            CookieContainer = pending.CookieContainer,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            IsActive = true
        };

        _sessionStore.SaveSession(sessionState);
        _sessionStore.RemovePendingChallenge(request.ChallengeToken);

        _logger.LogInformation("Challenge resolved. Successfully connected LinkedIn account {AccountId} for {Username}", accountId, pending.Username);

        return new ConnectAccountResponse
        {
            AccountId = accountId,
            Status = "connected",
            ConnectedAt = sessionState.CreatedAt,
            Profile = profileInfo
        };
    }

    public async Task<string> ResendChallengeCodeAsync(ResendChallengeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ChallengeToken))
        {
            throw new ArgumentException("Challenge token is required.", nameof(request.ChallengeToken));
        }

        var pending = _sessionStore.GetPendingChallenge(request.ChallengeToken);
        if (pending == null)
        {
            throw new ArgumentException("Challenge session not found or has expired. Please initiate connection again.");
        }

        // If challenge was created with Headless Browser, trigger browser resend
        if (pending.BrowserPage != null)
        {
            _logger.LogInformation("Triggering resend via Headless Browser for challenge {ChallengeToken}", request.ChallengeToken);
            return await _browserAuthService.ResendBrowserChallengeAsync(request.ChallengeToken, cancellationToken);
        }

        var resendUrl = pending.ResendUrl;
        if (string.IsNullOrEmpty(resendUrl))
        {
            // Construct default resend URL from challenge URL if needed
            resendUrl = $"{LinkedInBaseUrl}/checkpoint/challenge/resend";
        }

        var handler = new SocketsHttpHandler
        {
            CookieContainer = pending.CookieContainer,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        var resendReq = new HttpRequestMessage(HttpMethod.Get, resendUrl);
        resendReq.Headers.Referrer = new Uri(pending.ChallengeUrl);

        var response = await client.SendAsync(resendReq, cancellationToken);
        var responseHtml = await response.Content.ReadAsStringAsync(cancellationToken);

        // Update form fields if present in resend response
        var updatedFields = ExtractAllHiddenInputs(responseHtml);
        if (updatedFields.Count > 0)
        {
            foreach (var kvp in updatedFields) pending.FormFields[kvp.Key] = kvp.Value;
        }

        return "Verification code has been resent to your email/phone. Please check your inbox and spam folder.";
    }

    private async Task<ConnectAccountResponse> ConnectWithSessionCookieAsync(string rawCookie, string? inputJsessionId, CancellationToken cancellationToken)
    {
        var liAtCookie = rawCookie;
        var jsessionId = inputJsessionId;

        // Check if rawCookie is a full cookie string containing multiple cookies
        if (rawCookie.Contains('='))
        {
            var matchLiAt = Regex.Match(rawCookie, @"(?:^|;\s*)li_at=([^;]+)", RegexOptions.IgnoreCase);
            if (matchLiAt.Success)
            {
                liAtCookie = matchLiAt.Groups[1].Value.Trim();
            }

            if (string.IsNullOrEmpty(jsessionId))
            {
                var matchJsession = Regex.Match(rawCookie, @"(?:^|;\s*)JSESSIONID=([^;]+)", RegexOptions.IgnoreCase);
                if (matchJsession.Success)
                {
                    jsessionId = matchJsession.Groups[1].Value.Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(jsessionId))
        {
            jsessionId = $"ajax:{Guid.NewGuid():N}";
        }
        else
        {
            jsessionId = jsessionId.Trim().Trim('"');
        }

        var formattedJsessionId = $"\"{jsessionId}\"";

        var cookieContainer = new CookieContainer();
        cookieContainer.Add(new Uri(LinkedInBaseUrl), new Cookie("li_at", liAtCookie, "/", ".linkedin.com"));
        cookieContainer.Add(new Uri(LinkedInBaseUrl), new Cookie("JSESSIONID", formattedJsessionId, "/", ".linkedin.com"));
        cookieContainer.Add(new Uri(LinkedInBaseUrl), new Cookie("li_at", liAtCookie, "/", "www.linkedin.com"));
        cookieContainer.Add(new Uri(LinkedInBaseUrl), new Cookie("JSESSIONID", formattedJsessionId, "/", "www.linkedin.com"));

        var handler = new SocketsHttpHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        var profileInfo = await FetchProfileInfoAsync(client, cookieContainer, jsessionId, cancellationToken);
        var accountId = profileInfo.MemberUrn ?? $"acc_{Guid.NewGuid():N}";

        // Synchronize JSESSIONID if server returned or updated it
        var serverJsessionId = cookieContainer.GetAllCookies().FirstOrDefault(c => string.Equals(c.Name, "JSESSIONID", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrEmpty(serverJsessionId))
        {
            formattedJsessionId = serverJsessionId;
            jsessionId = serverJsessionId.Trim('"');
        }

        var sessionState = new LinkedInSessionState
        {
            AccountId = accountId,
            Username = profileInfo.PublicIdentifier ?? profileInfo.FullName ?? "linkedin_user",
            LiAtCookie = liAtCookie,
            JsessionId = formattedJsessionId,
            MemberUrn = profileInfo.MemberUrn,
            Profile = profileInfo,
            CookieContainer = cookieContainer,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            IsActive = true
        };

        _sessionStore.SaveSession(sessionState);
        _logger.LogInformation("Successfully connected LinkedIn account {AccountId} with session cookie", accountId);

        return new ConnectAccountResponse
        {
            AccountId = accountId,
            Status = "connected",
            ConnectedAt = sessionState.CreatedAt,
            Profile = profileInfo
        };
    }

    private async Task<ConnectedProfileInfo> FetchProfileInfoAsync(
        HttpClient client,
        CookieContainer cookieContainer,
        string jsessionId,
        CancellationToken cancellationToken)
    {
        var csrf = jsessionId.Trim('"');
        var request = new HttpRequestMessage(HttpMethod.Get, VoyagerMeUrl);
        request.Headers.Add("csrf-token", csrf);
        request.Headers.Add("x-restli-protocol-version", "2.0.0");
        request.Headers.Add("x-li-lang", "en_US");
        request.Headers.Add("Accept", "application/vnd.linkedin.normalized+json+2.1, application/json;q=0.9, */*;q=0.8");
        request.Headers.Referrer = new Uri("https://www.linkedin.com/feed/");
        
        var liAt = cookieContainer.GetAllCookies().FirstOrDefault(c => string.Equals(c.Name, "li_at", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrEmpty(liAt))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"li_at={liAt}; JSESSIONID=\"{csrf}\";");
        }

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Redirect || response.StatusCode == HttpStatusCode.Found)
            {
                throw new UnauthorizedAccessException("Failed to validate LinkedIn session. The session cookie (li_at) is invalid, expired, or redirected to login.");
            }

            throw new UnauthorizedAccessException($"LinkedIn session validation returned status {(int)response.StatusCode}. Please check your session cookie.");
        }

        var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsedProfile = ParseProfileInfoFromJson(jsonString);
        if (string.IsNullOrEmpty(parsedProfile.MemberUrn))
        {
            throw new UnauthorizedAccessException("Could not extract LinkedIn member information. The session cookie may be invalid or expired.");
        }

        return parsedProfile;
    }

    private static ConnectedProfileInfo ParseProfileInfoFromJson(string json)
    {
        var result = new ConnectedProfileInfo();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("plainId", out var plainId))
                {
                    result.MemberUrn = $"urn:li:member:{plainId.GetInt64()}";
                }
            }

            if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in included.EnumerateArray())
                {
                    if (item.TryGetProperty("$type", out var typeProp) &&
                        typeProp.GetString()?.Contains("MiniProfile", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (item.TryGetProperty("entityUrn", out var urn)) result.MemberUrn = urn.GetString();
                        if (item.TryGetProperty("firstName", out var fn)) result.FirstName = fn.GetString();
                        if (item.TryGetProperty("lastName", out var ln)) result.LastName = ln.GetString();
                        if (item.TryGetProperty("occupation", out var occ)) result.Headline = occ.GetString();
                        if (item.TryGetProperty("publicIdentifier", out var pubId)) result.PublicIdentifier = pubId.GetString();

                        result.FullName = $"{result.FirstName} {result.LastName}".Trim();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(result.MemberUrn) && root.TryGetProperty("entityUrn", out var directUrn))
            {
                result.MemberUrn = directUrn.GetString();
            }
        }
        catch
        {
            // Ignore JSON parse errors
        }

        return result;
    }

    private static (string ChallengeType, string Instructions) ExtractChallengeDetails(string html)
    {
        var challengeType = "EMAIL_PIN";

        // Extract primary headings & informative text from the page
        var textMatches = Regex.Matches(html, @"<(?:h1|h2|h3|p)[^>]*>([\s\S]*?)</(?:h1|h2|h3|p)>", RegexOptions.IgnoreCase);
        var messageParts = new List<string>();

        foreach (Match m in textMatches)
        {
            var txt = StripHtml(m.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(txt) && txt.Length > 5 && txt.Length < 300)
            {
                if (txt.Contains("ইমেল", StringComparison.OrdinalIgnoreCase) || 
                    txt.Contains("লিংক", StringComparison.OrdinalIgnoreCase) || 
                    txt.Contains("কোড", StringComparison.OrdinalIgnoreCase) || 
                    txt.Contains("email", StringComparison.OrdinalIgnoreCase) || 
                    txt.Contains("link", StringComparison.OrdinalIgnoreCase) || 
                    txt.Contains("code", StringComparison.OrdinalIgnoreCase) ||
                    txt.Contains("pin", StringComparison.OrdinalIgnoreCase) ||
                    txt.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
                    txt.Contains("স্প্যাম", StringComparison.OrdinalIgnoreCase))
                {
                    if (!messageParts.Contains(txt)) messageParts.Add(txt);
                }
            }
        }

        var instructions = messageParts.Count > 0 ? string.Join(" - ", messageParts.Take(3)) : string.Empty;

        if (html.Contains("ওয়ান-টাইম লিংক", StringComparison.OrdinalIgnoreCase) || 
            html.Contains("one-time link", StringComparison.OrdinalIgnoreCase) || 
            html.Contains("magic link", StringComparison.OrdinalIgnoreCase))
        {
            challengeType = "ONE_TIME_EMAIL_LINK";
        }
        else if (html.Contains("authenticator", StringComparison.OrdinalIgnoreCase) || html.Contains("TOTP", StringComparison.OrdinalIgnoreCase))
        {
            challengeType = "AUTHENTICATOR_APP";
        }
        else if (html.Contains("phone", StringComparison.OrdinalIgnoreCase) || html.Contains("SMS", StringComparison.OrdinalIgnoreCase))
        {
            challengeType = "SMS_PIN";
        }
        else if (html.Contains("email", StringComparison.OrdinalIgnoreCase) || html.Contains("ইমেল", StringComparison.OrdinalIgnoreCase))
        {
            challengeType = "EMAIL_PIN";
        }

        if (string.IsNullOrWhiteSpace(instructions))
        {
            instructions = $"LinkedIn security verification required ({challengeType}). LinkedIn has sent a verification email to your primary email address. Please check your inbox and spam folder.";
        }

        return (challengeType, instructions);
    }

    private static string? ExtractResendUrl(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        var match = Regex.Match(html, @"(?:href|data-resend-url)=[""']([^""']*(?:resend|send-code|challenge\/resend)[^""']*)[""']", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var raw = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (!raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                raw = $"{LinkedInBaseUrl}{(raw.StartsWith('/') ? "" : "/")}{raw}";
            }
            return raw;
        }

        return null;
    }

    private static string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var text = Regex.Replace(input, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string? ExtractInputValue(string html, string inputName)
    {
        if (string.IsNullOrEmpty(html)) return null;

        var pattern = $@"<input[^>]+name=[""']{Regex.Escape(inputName)}[""'][^>]+value=[""']([^""']*)[""']";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }

        var altPattern = $@"<input[^>]+value=[""']([^""']*)[""'][^>]+name=[""']{Regex.Escape(inputName)}[""']";
        var altMatch = Regex.Match(html, altPattern, RegexOptions.IgnoreCase);
        return altMatch.Success ? WebUtility.HtmlDecode(altMatch.Groups[1].Value) : null;
    }

    private static Dictionary<string, string> ExtractAllHiddenInputs(string html)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(html)) return dict;

        var matches = Regex.Matches(html, @"<input[^>]+>", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var tag = m.Value;
            var isHidden = Regex.IsMatch(tag, @"type=[""']hidden[""']", RegexOptions.IgnoreCase);
            if (!isHidden) continue;

            var nameMatch = Regex.Match(tag, @"name=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            var valueMatch = Regex.Match(tag, @"value=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            if (nameMatch.Success)
            {
                var name = nameMatch.Groups[1].Value;
                var val = valueMatch.Success ? WebUtility.HtmlDecode(valueMatch.Groups[1].Value) : string.Empty;
                dict[name] = val;
            }
        }
        return dict;
    }

    private static string? ExtractFormAction(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var match = Regex.Match(html, @"<form[^>]+action=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }
}
