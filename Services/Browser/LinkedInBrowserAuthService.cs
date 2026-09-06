using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuppeteerSharp;
using LinkdUnified.Models;

namespace LinkdUnified.Services.Browser;

public class LinkedInBrowserAuthService : ILinkedInBrowserAuthService
{
    private readonly ILinkedInSessionStore _sessionStore;
    private readonly ILogger<LinkedInBrowserAuthService> _logger;
    private static bool _browserDownloaded = false;
    private static readonly SemaphoreSlim _browserDownloadLock = new(1, 1);

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private const string VoyagerMeUrl = "https://www.linkedin.com/voyager/api/me";

    public LinkedInBrowserAuthService(ILinkedInSessionStore sessionStore, ILogger<LinkedInBrowserAuthService> logger)
    {
        _sessionStore = sessionStore;
        _logger = logger;
    }

    private static string? GetChromeExecutablePath()
    {
        var paths = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
        };

        return paths.FirstOrDefault(File.Exists);
    }

    private static async Task EnsureBrowserDownloadedAsync()
    {
        if (_browserDownloaded || GetChromeExecutablePath() != null) return;

        await _browserDownloadLock.WaitAsync();
        try
        {
            if (!_browserDownloaded && GetChromeExecutablePath() == null)
            {
                var browserFetcher = new BrowserFetcher();
                await browserFetcher.DownloadAsync();
                _browserDownloaded = true;
            }
        }
        finally
        {
            _browserDownloadLock.Release();
        }
    }

    public async Task<ConnectAccountResult> LoginWithBrowserAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        await EnsureBrowserDownloadedAsync();

        var chromePath = GetChromeExecutablePath();
        _logger.LogInformation("Launching Chrome for LinkedIn authentication of {Username} (Executable: {Path})", username, chromePath ?? "Bundled Chromium");

        var launchOptions = new LaunchOptions
        {
            Headless = true,
            ExecutablePath = chromePath,
            Args = new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-blink-features=AutomationControlled",
                "--disable-infobars",
                "--window-size=1280,800"
            }
        };

        var browser = await Puppeteer.LaunchAsync(launchOptions);
        var page = await browser.NewPageAsync();

        // Evade basic bot detection
        await page.EvaluateFunctionOnNewDocumentAsync(@"() => {
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            window.chrome = { runtime: {} };
        }");

        await page.SetUserAgentAsync(UserAgent);
        await page.SetViewportAsync(new ViewPortOptions { Width = 1280, Height = 800 });

        try
        {
            _logger.LogInformation("Navigating to LinkedIn login page for {Username}", username);
            await page.GoToAsync("https://www.linkedin.com/login", new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Networkidle2 },
                Timeout = 30000
            });

            var initialUrl = page.Url;
            _logger.LogInformation("Landed on URL: {Url}", initialUrl);

            // Wait for login inputs to become ready
            await page.WaitForFunctionAsync(@"() => {
                const u = Array.from(document.querySelectorAll('input')).find(i => (i.type === 'email' || i.type === 'text') && i.offsetParent !== null);
                const p = Array.from(document.querySelectorAll('input')).find(i => i.type === 'password' && i.offsetParent !== null);
                return u !== null && p !== null;
            }", new WaitForFunctionOptions { Timeout = 20000 });

            _logger.LogInformation("Setting login credentials for {Username}", username);

            // Set inputs using React native value setter
            await page.EvaluateFunctionAsync(@"(user, pass) => {
                const userInput = Array.from(document.querySelectorAll('input')).find(i => (i.type === 'email' || i.type === 'text') && i.offsetParent !== null);
                const passInput = Array.from(document.querySelectorAll('input')).find(i => i.type === 'password' && i.offsetParent !== null);

                if (!userInput || !passInput) return false;

                const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;

                userInput.focus();
                nativeSetter.call(userInput, user);
                userInput.dispatchEvent(new Event('input', { bubbles: true }));
                userInput.dispatchEvent(new Event('change', { bubbles: true }));

                passInput.focus();
                nativeSetter.call(passInput, pass);
                passInput.dispatchEvent(new Event('input', { bubbles: true }));
                passInput.dispatchEvent(new Event('change', { bubbles: true }));

                return true;
            }", username, password);

            _logger.LogInformation("Clicking exact LinkedIn Sign In button for {Username}", username);

            // Click exact Sign In button (ignoring Microsoft/Apple/Google SSO buttons)
            var btnHandle = await page.EvaluateFunctionHandleAsync(@"() => {
                const btn = Array.from(document.querySelectorAll('button')).find(b => {
                    if (!b || b.offsetParent === null) return false;
                    const txt = b.innerText.trim();
                    const isSocial = txt.includes('Microsoft') || txt.includes('Apple') || txt.includes('Google');
                    if (isSocial) return false;

                    return b.type === 'submit' || 
                           txt === 'Sign in' || 
                           txt === 'সাইন ইন করুন' || 
                           txt === 'সাইন ইন' || 
                           txt === 'Log in' || 
                           txt === 'লগ ইন' ||
                           b.getAttribute('data-litms-control-urn') === 'login-submit' ||
                           b.className.includes('a22407db');
                });
                return btn;
            }");

            if (btnHandle is IElementHandle el && el != null)
            {
                await el.ClickAsync();
            }
            else
            {
                await page.Keyboard.PressAsync("Enter");
            }

            _logger.LogInformation("Waiting for LinkedIn post-login state transition for {Username}...", username);

            // Intelligent state resolution loop (up to 20 seconds)
            var startWait = DateTime.UtcNow;
            while ((DateTime.UtcNow - startWait).TotalSeconds < 20)
            {
                await Task.Delay(1000, cancellationToken);

                var currentUrl = page.Url;
                var cookies = await page.GetCookiesAsync();
                var liAt = cookies.FirstOrDefault(c => string.Equals(c.Name, "li_at", StringComparison.OrdinalIgnoreCase))?.Value;
                var jsessionId = cookies.FirstOrDefault(c => string.Equals(c.Name, "JSESSIONID", StringComparison.OrdinalIgnoreCase))?.Value ?? $"ajax:{Guid.NewGuid():N}";

                // 1. Success condition: li_at cookie obtained
                if (!string.IsNullOrEmpty(liAt))
                {
                    _logger.LogInformation("LinkedIn session cookie (li_at) captured successfully for {Username}", username);
                    var connectedAccount = await FinalizeSessionFromCookiesAsync(username, liAt, jsessionId, cookies, cancellationToken);

                    await page.CloseAsync();
                    await browser.CloseAsync();

                    return new ConnectAccountResult
                    {
                        Status = "connected",
                        RequiresChallenge = false,
                        Account = connectedAccount,
                        Message = "LinkedIn account connected successfully via Headless Browser Auth."
                    };
                }

                bool hasPinInput = false;
                string? visibleError = null;
                bool hasCaptcha = false;

                try
                {
                    hasPinInput = await page.EvaluateFunctionAsync<bool>(@"() => {
                        return document.querySelector('input[name=""pin""], input[name=""verificationCode""], #input__email_verification_pin, #pin, input[type=""tel""], input[type=""number""]') !== null;
                    }");

                    visibleError = await page.EvaluateFunctionAsync<string?>(@"() => {
                        const errorEl = document.querySelector('#error-for-username, #error-for-password, .alert-content, .form__label--error, div[alert-type=""error""], .artdeco-inline-feedback--error, .error-for-password');
                        return errorEl && errorEl.innerText.trim().length > 0 ? errorEl.innerText.trim() : null;
                    }");

                    hasCaptcha = await page.EvaluateFunctionAsync<bool>(@"() => {
                        return document.querySelector('iframe[src*=""arkose""], iframe[src*=""captcha""], #captcha-internal, .captcha-challenge') !== null;
                    }");
                }
                catch
                {
                    // Ignore DOM evaluation during page transition / redirect
                }

                // 2. 2FA / Checkpoint / Challenge detection
                if (currentUrl.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) ||
                    currentUrl.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
                    currentUrl.Contains("identity", StringComparison.OrdinalIgnoreCase) ||
                    hasPinInput)
                {
                    _logger.LogInformation("LinkedIn 2FA Checkpoint triggered for {Username} at {Url}", username, currentUrl);

                    // If app challenge with "no access to device" or "verify another way" button exists, click it to dispatch email code
                    try
                    {
                        await page.EvaluateFunctionAsync(@"() => {
                            const btns = Array.from(document.querySelectorAll('button, a'));
                            const altBtn = btns.find(b => {
                                const t = b.innerText;
                                return t.includes('অ্যাক্সেস নেই') || t.includes('অন্য উপায়ে') || t.includes('access') || t.includes('another way') || t.includes('email') || t.includes('ইমেল');
                            });
                            if (altBtn) altBtn.click();
                        }");
                        await Task.Delay(1500, cancellationToken);
                    }
                    catch { }

                    var pageContent = await SafeGetContentAsync(page);
                    var (challengeType, instructions) = ExtractChallengeDetails(pageContent);

                    var challengeToken = Guid.NewGuid().ToString("N");
                    var pendingState = new PendingChallengeState
                    {
                        ChallengeToken = challengeToken,
                        Username = username,
                        ChallengeUrl = currentUrl,
                        ChallengeType = challengeType,
                        BrowserPage = page,
                        BrowserInstance = browser,
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

                // 3. Error detection
                if (!string.IsNullOrWhiteSpace(visibleError))
                {
                    _logger.LogWarning("LinkedIn login error displayed: {Error}", visibleError);
                    await page.CloseAsync();
                    await browser.CloseAsync();
                    throw new UnauthorizedAccessException($"LinkedIn authentication failed: {visibleError}");
                }

                // 4. CAPTCHA detection
                if (hasCaptcha)
                {
                    _logger.LogWarning("LinkedIn CAPTCHA triggered for {Username}", username);
                    await page.CloseAsync();
                    await browser.CloseAsync();
                    throw new UnauthorizedAccessException("LinkedIn has triggered a visual security CAPTCHA check. Please connect using your sessionCookie (li_at) from an active browser session.");
                }
            }

            var finalPageUrl = page.Url;
            _logger.LogWarning("Post-login timeout reached for {Username}. URL: {Url}", username, finalPageUrl);

            await page.CloseAsync();
            await browser.CloseAsync();
            throw new UnauthorizedAccessException($"LinkedIn authentication did not return a valid session. Page URL: {finalPageUrl}. Please check credentials.");
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Error in Headless Browser login for {Username}", username);
            try { await page.CloseAsync(); } catch { }
            try { await browser.CloseAsync(); } catch { }
            throw;
        }
    }

    public async Task<ConnectAccountResponse> SubmitBrowserChallengeAsync(string challengeToken, string pinCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinCode);

        var pending = _sessionStore.GetPendingChallenge(challengeToken);
        if (pending == null)
        {
            throw new ArgumentException("Verification challenge session not found or has expired. Please initiate login again.");
        }

        if (pending.BrowserPage is not IPage page || pending.BrowserInstance is not IBrowser browser)
        {
            throw new InvalidOperationException("Active browser session not found for this challenge. Please initiate login again.");
        }

        try
        {
            _logger.LogInformation("Submitting 6-digit PIN code for challenge {ChallengeToken} (User: {Username})", challengeToken, pending.Username);

            // Wait for PIN input
            await page.WaitForFunctionAsync(@"() => {
                const pin = document.querySelector('input[name=""pin""]') || document.querySelector('input[name=""verificationCode""]') || document.querySelector('#input__email_verification_pin') || document.querySelector('#pin') || document.querySelector('input[type=""tel""]') || document.querySelector('input[type=""number""]');
                return pin !== null;
            }", new WaitForFunctionOptions { Timeout = 15000 });

            // Enter PIN and submit using React native setter
            await page.EvaluateFunctionAsync(@"(code) => {
                const pin = document.querySelector('input[name=""pin""]') || document.querySelector('input[name=""verificationCode""]') || document.querySelector('#input__email_verification_pin') || document.querySelector('#pin') || document.querySelector('input[type=""tel""]') || document.querySelector('input[type=""number""]');
                if (pin) {
                    pin.focus();
                    const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
                    if (nativeSetter) nativeSetter.call(pin, code);
                    else pin.value = code;
                    pin.dispatchEvent(new Event('input', { bubbles: true }));
                    pin.dispatchEvent(new Event('change', { bubbles: true }));
                }

                const btn = Array.from(document.querySelectorAll('button')).find(b => {
                    if (!b || b.offsetParent === null) return false;
                    const txt = b.innerText.trim();
                    return b.type === 'submit' || txt.includes('Submit') || txt.includes('Verify') || txt.includes('যাচাই') || txt.includes('জমা') || txt.includes('Sign in') || txt.includes('সাইন ইন');
                });

                if (btn) btn.click();
            }", pinCode.Trim());

            // Poll for post-PIN resolution (up to 20 seconds)
            var startWait = DateTime.UtcNow;
            while ((DateTime.UtcNow - startWait).TotalSeconds < 20)
            {
                await Task.Delay(1000, cancellationToken);
                var cookies = await page.GetCookiesAsync();
                var liAt = cookies.FirstOrDefault(c => string.Equals(c.Name, "li_at", StringComparison.OrdinalIgnoreCase))?.Value;
                var jsessionId = cookies.FirstOrDefault(c => string.Equals(c.Name, "JSESSIONID", StringComparison.OrdinalIgnoreCase))?.Value ?? $"ajax:{Guid.NewGuid():N}";

                if (!string.IsNullOrEmpty(liAt))
                {
                    var accountResponse = await FinalizeSessionFromCookiesAsync(pending.Username, liAt, jsessionId, cookies, cancellationToken);
                    _sessionStore.RemovePendingChallenge(challengeToken);

                    await page.CloseAsync();
                    await browser.CloseAsync();

                    return accountResponse;
                }
            }

            var finalContent = await SafeGetContentAsync(page);
            if (finalContent.Contains("ভুল") || finalContent.Contains("incorrect") || finalContent.Contains("invalid"))
            {
                throw new UnauthorizedAccessException("The PIN code entered is incorrect or expired. Please check your email and try again.");
            }

            throw new UnauthorizedAccessException("LinkedIn verification failed. Please check the PIN code entered.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit browser challenge for {Username}", pending.Username);
            try { await page.CloseAsync(); } catch { }
            try { await browser.CloseAsync(); } catch { }
            _sessionStore.RemovePendingChallenge(challengeToken);
            throw;
        }
    }

    public async Task<string> ResendBrowserChallengeAsync(string challengeToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeToken);

        var pending = _sessionStore.GetPendingChallenge(challengeToken);
        if (pending == null)
        {
            throw new ArgumentException("Challenge session not found or has expired. Please initiate login again.");
        }

        if (pending.BrowserPage is not IPage page)
        {
            throw new InvalidOperationException("Active browser session not found for this challenge.");
        }

        try
        {
            _logger.LogInformation("Triggering browser resend OTP button for {Username}", pending.Username);

            var resendBtnSelector = "#btn-resend-otp, button.resend-button, a[data-control-name='resend_code'], button:has-text('Resend'), button:has-text('পুনরায়')";
            var resendBtn = await page.QuerySelectorAsync(resendBtnSelector);
            if (resendBtn != null)
            {
                await resendBtn.ClickAsync();
                return "Verification PIN has been resent to your email. Please check your inbox and spam folder.";
            }

            return "Verification PIN has been requested. Please check your inbox and spam folder.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to click resend button in browser for {Username}", pending.Username);
            return "Resend request submitted. Please check your email.";
        }
    }

    private async Task<ConnectAccountResponse> FinalizeSessionFromCookiesAsync(
        string username,
        string liAt,
        string jsessionId,
        PuppeteerSharp.CookieParam[] browserCookies,
        CancellationToken cancellationToken)
    {
        var cookieContainer = new CookieContainer();
        var cleanJsession = jsessionId.Trim('"');
        var formattedJsession = $"\"{cleanJsession}\"";

        cookieContainer.Add(new Uri("https://www.linkedin.com"), new Cookie("li_at", liAt, "/", ".linkedin.com"));
        cookieContainer.Add(new Uri("https://www.linkedin.com"), new Cookie("JSESSIONID", formattedJsession, "/", ".linkedin.com"));
        cookieContainer.Add(new Uri("https://www.linkedin.com"), new Cookie("li_at", liAt, "/", "www.linkedin.com"));
        cookieContainer.Add(new Uri("https://www.linkedin.com"), new Cookie("JSESSIONID", formattedJsession, "/", "www.linkedin.com"));

        // Add additional browser cookies if available (e.g. bcookie, bscookie)
        foreach (var c in browserCookies)
        {
            try
            {
                cookieContainer.Add(new Uri("https://www.linkedin.com"), new Cookie(c.Name, c.Value, c.Path ?? "/", c.Domain ?? ".linkedin.com"));
            }
            catch { }
        }

        // Fetch profile
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false
        };

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        var profileReq = new HttpRequestMessage(HttpMethod.Get, VoyagerMeUrl);
        profileReq.Headers.Add("csrf-token", cleanJsession);
        profileReq.Headers.Add("x-restli-protocol-version", "2.0.0");
        profileReq.Headers.Add("x-li-lang", "en_US");
        profileReq.Headers.Add("Accept", "application/vnd.linkedin.normalized+json+2.1, application/json;q=0.9, */*;q=0.8");
        profileReq.Headers.Referrer = new Uri("https://www.linkedin.com/feed/");
        profileReq.Headers.TryAddWithoutValidation("Cookie", $"li_at={liAt}; JSESSIONID=\"{cleanJsession}\";");

        var profileResp = await client.SendAsync(profileReq, cancellationToken);
        var profileJson = await profileResp.Content.ReadAsStringAsync(cancellationToken);

        var profileInfo = ParseProfileInfoFromJson(profileJson);
        var accountId = profileInfo.MemberUrn ?? $"acc_{Guid.NewGuid():N}";

        var sessionState = new LinkedInSessionState
        {
            AccountId = accountId,
            Username = profileInfo.PublicIdentifier ?? profileInfo.FullName ?? username,
            LiAtCookie = liAt,
            JsessionId = formattedJsession,
            MemberUrn = profileInfo.MemberUrn,
            Profile = profileInfo,
            CookieContainer = cookieContainer,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            IsActive = true
        };

        _sessionStore.SaveSession(sessionState);
        _logger.LogInformation("Successfully connected LinkedIn account {AccountId} for {Username} via Headless Browser", accountId, username);

        return new ConnectAccountResponse
        {
            AccountId = accountId,
            Status = "connected",
            ConnectedAt = sessionState.CreatedAt,
            Profile = profileInfo
        };
    }

    private static ConnectedProfileInfo ParseProfileInfoFromJson(string json)
    {
        var result = new ConnectedProfileInfo();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("plainId", out var plainId))
            {
                result.MemberUrn = $"urn:li:member:{plainId.GetInt64()}";
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
        catch { }

        return result;
    }

    private static (string ChallengeType, string Instructions) ExtractChallengeDetails(string html)
    {
        var challengeType = "EMAIL_PIN";

        var textMatches = Regex.Matches(html, @"<(?:h1|h2|h3|p)[^>]*>([\s\S]*?)</(?:h1|h2|h3|p)>", RegexOptions.IgnoreCase);
        var messageParts = new List<string>();

        foreach (Match m in textMatches)
        {
            var txt = Regex.Replace(m.Groups[1].Value, @"<[^>]+>", " ").Trim();
            txt = WebUtility.HtmlDecode(txt);
            txt = Regex.Replace(txt, @"\s+", " ").Trim();

            if (!string.IsNullOrWhiteSpace(txt) && txt.Length > 5 && txt.Length < 300)
            {
                if (!messageParts.Contains(txt)) messageParts.Add(txt);
            }
        }

        var instructions = messageParts.Count > 0 ? string.Join(" - ", messageParts.Take(2)) : "LinkedIn 2FA Verification required. A 6-digit PIN code has been sent to your email.";

        if (html.Contains("authenticator", StringComparison.OrdinalIgnoreCase) || html.Contains("TOTP", StringComparison.OrdinalIgnoreCase))
        {
            challengeType = "AUTHENTICATOR_APP";
        }
        else if (html.Contains("phone", StringComparison.OrdinalIgnoreCase) || html.Contains("SMS", StringComparison.OrdinalIgnoreCase))
        {
            challengeType = "SMS_PIN";
        }
        else
        {
            challengeType = "EMAIL_PIN";
        }

        return (challengeType, instructions);
    }

    private static async Task<string> SafeGetContentAsync(IPage page)
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                return await page.GetContentAsync();
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        return string.Empty;
    }
}
