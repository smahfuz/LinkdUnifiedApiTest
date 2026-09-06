using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using LinkdUnified.Models;
using LinkdUnified.Services.Browser;

namespace LinkdUnified.Services;

public class LinkedInMessagingService : ILinkedInMessagingService
{
    private readonly ILinkedInSessionStore _sessionStore;
    private readonly ILinkedInBrowserAuthService _browserAuthService;
    private readonly ILogger<LinkedInMessagingService> _logger;

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
    private const string VoyagerConversationsUrl = "https://www.linkedin.com/voyager/api/messaging/conversations";
    private const string LegacyVoyagerConversationsUrl = "https://www.linkedin.com/voyager/api/messaging/conversations?keyVersion=LEGACY_INBOX";
    private const string VoyagerBaseUrl = "https://www.linkedin.com";

    public LinkedInMessagingService(
        ILinkedInSessionStore sessionStore,
        ILinkedInBrowserAuthService browserAuthService,
        ILogger<LinkedInMessagingService> logger)
    {
        _sessionStore = sessionStore;
        _browserAuthService = browserAuthService;
        _logger = logger;
    }

    public async Task<ConversationListResponse> GetConversationsAsync(string? accountId = null, CancellationToken cancellationToken = default)
    {
        var session = GetValidSession(accountId);
        EnsureCookieContainerComplete(session);

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false, // We manually handle redirects to ensure custom headers and cookies are preserved
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        _logger.LogInformation("Fetching conversations for account {AccountId}", session.AccountId);

        var response = await SendVoyagerRequestWithRedirectsAsync(client, VoyagerConversationsUrl, session, cancellationToken);
        
        // If clean URL failed, try legacy URL
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Clean conversations URL returned {Status}. Trying legacy URL.", response.StatusCode);
            response = await SendVoyagerRequestWithRedirectsAsync(client, LegacyVoyagerConversationsUrl, session, cancellationToken);
        }

        string json;
        if (response.IsSuccessStatusCode)
        {
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        else
        {
            _logger.LogWarning("Voyager HTTP conversations request failed ({Status}). Falling back to browser context fetch.", response.StatusCode);
            try
            {
                json = await _browserAuthService.FetchWithBrowserAsync(VoyagerConversationsUrl, session, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Browser fallback fetch for clean conversations URL failed, trying legacy URL...");
                try
                {
                    json = await _browserAuthService.FetchWithBrowserAsync(LegacyVoyagerConversationsUrl, session, cancellationToken);
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "Browser fallback fetch failed for both conversations URLs");
                    var httpBody = string.Empty;
                    try { httpBody = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
                    throw new HttpRequestException($"LinkedIn Voyager API failed (HTTP {(int)response.StatusCode}: {response.ReasonPhrase}, Body: {httpBody.Substring(0, Math.Min(300, httpBody.Length))}). Browser fallback error: {ex2.Message}", ex2);
                }
            }
        }

        session.LastUsedAt = DateTime.UtcNow;

        return ParseConversationsFromJson(json, session);
    }

    public async Task<MessageListResponse> GetMessagesAsync(string conversationId, string? accountId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("Conversation ID cannot be empty.", nameof(conversationId));
        }

        var session = GetValidSession(accountId);
        EnsureCookieContainerComplete(session);

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        // Normalize conversation URN if a simple ID was passed
        var formattedUrn = conversationId.StartsWith("urn:li:")
            ? conversationId
            : $"urn:li:fs_conversation:{conversationId}";

        var encodedUrn = Uri.EscapeDataString(formattedUrn);
        var messagesUrl = $"{VoyagerBaseUrl}/voyager/api/messaging/conversations/{encodedUrn}/events";

        _logger.LogInformation("Fetching messages for conversation {ConversationId} (Account: {AccountId})", conversationId, session.AccountId);

        var response = await SendVoyagerRequestWithRedirectsAsync(client, messagesUrl, session, cancellationToken);
        
        string json;
        if (response.IsSuccessStatusCode)
        {
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        else
        {
            _logger.LogWarning("Voyager HTTP messages request failed ({Status}). Falling back to browser context fetch.", response.StatusCode);
            try
            {
                json = await _browserAuthService.FetchWithBrowserAsync(messagesUrl, session, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Browser fallback fetch failed for messages");
                var httpBody = string.Empty;
                try { httpBody = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
                throw new HttpRequestException($"LinkedIn Voyager messages API failed (HTTP {(int)response.StatusCode}: {response.ReasonPhrase}, Body: {httpBody.Substring(0, Math.Min(300, httpBody.Length))}). Browser fallback error: {ex.Message}", ex);
            }
        }

        session.LastUsedAt = DateTime.UtcNow;

        return ParseMessagesFromJson(json, conversationId, session);
    }

    private async Task<HttpResponseMessage> SendVoyagerRequestWithRedirectsAsync(
        HttpClient client,
        string initialUrl,
        LinkedInSessionState session,
        CancellationToken cancellationToken)
    {
        var currentUrl = initialUrl;
        const int maxRedirects = 6;
        var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < maxRedirects; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            AddVoyagerHeaders(request, session);

            var response = await client.SendAsync(request, cancellationToken);

            // Extract updated cookies if LinkedIn sent Set-Cookie
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
            {
                foreach (var header in setCookieHeaders)
                {
                    var matchJsession = System.Text.RegularExpressions.Regex.Match(header, @"JSESSIONID=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (matchJsession.Success)
                    {
                        var newJsession = matchJsession.Groups[1].Value.Trim().Trim('"');
                        session.JsessionId = $"\"{newJsession}\"";
                    }
                    var matchLiAt = System.Text.RegularExpressions.Regex.Match(header, @"li_at=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (matchLiAt.Success)
                    {
                        session.LiAtCookie = matchLiAt.Groups[1].Value.Trim();
                    }
                }
            }

            if ((int)response.StatusCode >= 300 && (int)response.StatusCode <= 399)
            {
                var location = response.Headers.Location;
                if (location == null)
                {
                    return response;
                }

                var nextUrl = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(currentUrl), location).ToString();
                _logger.LogInformation("Voyager redirect ({StatusCode}): {CurrentUrl} -> {NextUrl}", response.StatusCode, currentUrl, nextUrl);

                if (nextUrl.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                    nextUrl.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) ||
                    nextUrl.Contains("uas/authenticate", StringComparison.OrdinalIgnoreCase))
                {
                    _sessionStore.InvalidateSession(session.AccountId);
                    throw new UnauthorizedAccessException($"LinkedIn session is unauthorized or expired (Redirected to login/checkpoint: {nextUrl}).");
                }

                // Prevent infinite loop if redirecting to same URL
                if (visitedUrls.Contains(nextUrl) && string.Equals(nextUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("LinkedIn Voyager redirected to the same URL {Url} repeatedly. Proceeding to fallback.", nextUrl);
                    return response;
                }

                visitedUrls.Add(currentUrl);
                currentUrl = nextUrl;
                continue;
            }

            return response;
        }

        throw new HttpRequestException("Too many redirects while contacting LinkedIn Voyager API.");
    }

    private LinkedInSessionState GetValidSession(string? accountId)
    {
        var session = _sessionStore.GetSession(accountId);
        if (session == null || !session.IsActive)
        {
            throw new UnauthorizedAccessException(
                string.IsNullOrWhiteSpace(accountId)
                    ? "No active LinkedIn account session found. Please connect an account first."
                    : $"LinkedIn account '{accountId}' is not connected or session has expired.");
        }
        return session;
    }

    private static void EnsureCookieContainerComplete(LinkedInSessionState session)
    {
        // Sync JSESSIONID from cookie container
        var latestJsessionId = session.CookieContainer.GetAllCookies()
            .FirstOrDefault(c => string.Equals(c.Name, "JSESSIONID", StringComparison.OrdinalIgnoreCase))?.Value;

        if (!string.IsNullOrEmpty(latestJsessionId))
        {
            session.JsessionId = latestJsessionId;
        }

        // Ensure both domains have the required cookies
        var baseUri = new Uri(VoyagerBaseUrl);
        session.CookieContainer.Add(baseUri, new Cookie("li_at", session.LiAtCookie, "/", ".linkedin.com"));
        session.CookieContainer.Add(baseUri, new Cookie("JSESSIONID", session.JsessionId, "/", ".linkedin.com"));
        session.CookieContainer.Add(baseUri, new Cookie("li_at", session.LiAtCookie, "/", "www.linkedin.com"));
        session.CookieContainer.Add(baseUri, new Cookie("JSESSIONID", session.JsessionId, "/", "www.linkedin.com"));
    }

    private static void AddVoyagerHeaders(HttpRequestMessage request, LinkedInSessionState session)
    {
        var csrf = session.GetCsrfToken();
        request.Headers.Add("csrf-token", csrf);
        request.Headers.Add("x-restli-protocol-version", "2.0.0");
        request.Headers.Add("x-li-lang", "en_US");
        request.Headers.Add("Accept", "application/vnd.linkedin.normalized+json+2.1, application/json;q=0.9, */*;q=0.8");
        request.Headers.Add("sec-fetch-dest", "empty");
        request.Headers.Add("sec-fetch-mode", "cors");
        request.Headers.Add("sec-fetch-site", "same-origin");
        request.Headers.Referrer = new Uri("https://www.linkedin.com/messaging/");
        
        var cookieHeader = !string.IsNullOrWhiteSpace(session.RawCookies)
            ? session.RawCookies
            : $"li_at={session.LiAtCookie}; JSESSIONID=\"{csrf}\";";

        if (!cookieHeader.Contains("JSESSIONID="))
        {
            cookieHeader = $"JSESSIONID=\"{csrf}\"; {cookieHeader}";
        }
        if (!cookieHeader.Contains("li_at="))
        {
            cookieHeader = $"li_at={session.LiAtCookie}; {cookieHeader}";
        }

        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
    }

    private void HandleErrorResponse(HttpResponseMessage response, string accountId)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            _sessionStore.InvalidateSession(accountId);
            _logger.LogWarning("LinkedIn session expired or unauthorized for account {AccountId}", accountId);
            throw new UnauthorizedAccessException("LinkedIn session has expired or is invalid. Please reconnect the account.");
        }

        throw new HttpRequestException($"LinkedIn API returned error status: {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private static ConversationListResponse ParseConversationsFromJson(string json, LinkedInSessionState session)
    {
        var response = new ConversationListResponse
        {
            AccountId = session.AccountId,
            Conversations = new List<ConversationDto>()
        };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Direct format check (from DOM extraction or clean response)
            if (root.TryGetProperty("conversations", out var directConvs) && directConvs.ValueKind == JsonValueKind.Array)
            {
                var directList = JsonSerializer.Deserialize<List<ConversationDto>>(directConvs.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (directList != null)
                {
                    response.Conversations = directList;
                    response.Total = directList.Count;
                    return response;
                }
            }

            // Map included profiles and messages by entityUrn
            var miniProfiles = new Dictionary<string, ParticipantDto>(StringComparer.OrdinalIgnoreCase);
            var messagingMembers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Member URN -> MiniProfile URN
            var messageEvents = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in included.EnumerateArray())
                {
                    if (item.TryGetProperty("$type", out var typeProp))
                    {
                        var typeName = typeProp.GetString() ?? string.Empty;

                        if (typeName.Contains("MiniProfile", StringComparison.OrdinalIgnoreCase))
                        {
                            var urn = item.TryGetProperty("entityUrn", out var u) ? u.GetString() : null;
                            if (!string.IsNullOrEmpty(urn))
                            {
                                var firstName = item.TryGetProperty("firstName", out var fn) ? fn.GetString() : string.Empty;
                                var lastName = item.TryGetProperty("lastName", out var ln) ? ln.GetString() : string.Empty;
                                var participant = new ParticipantDto
                                {
                                    ProfileUrn = urn,
                                    Name = $"{firstName} {lastName}".Trim(),
                                    Headline = item.TryGetProperty("occupation", out var occ) ? occ.GetString() : null,
                                    PublicIdentifier = item.TryGetProperty("publicIdentifier", out var pub) ? pub.GetString() : null
                                };
                                miniProfiles[urn] = participant;
                            }
                        }
                        else if (typeName.Contains("MessagingMember", StringComparison.OrdinalIgnoreCase))
                        {
                            var urn = item.TryGetProperty("entityUrn", out var u) ? u.GetString() : null;
                            var miniProfileUrn = item.TryGetProperty("miniProfile", out var mp) ? mp.GetString() : null;
                            if (!string.IsNullOrEmpty(urn) && !string.IsNullOrEmpty(miniProfileUrn))
                            {
                                messagingMembers[urn] = miniProfileUrn;
                            }
                        }
                        else if (typeName.Contains("Event", StringComparison.OrdinalIgnoreCase))
                        {
                            var urn = item.TryGetProperty("entityUrn", out var u) ? u.GetString() : null;
                            if (!string.IsNullOrEmpty(urn))
                            {
                                messageEvents[urn] = item.Clone();
                            }
                        }
                    }
                }
            }

            // Enumerate conversations in data.elements or elements
            var convElements = new List<JsonElement>();
            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("elements", out var dElems) && dElems.ValueKind == JsonValueKind.Array)
            {
                convElements.AddRange(dElems.EnumerateArray());
            }
            else if (root.TryGetProperty("elements", out var rElems) && rElems.ValueKind == JsonValueKind.Array)
            {
                convElements.AddRange(rElems.EnumerateArray());
            }

            foreach (var convElem in convElements)
            {
                var entityUrn = convElem.TryGetProperty("entityUrn", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                var rawId = entityUrn.StartsWith("urn:li:fs_conversation:")
                    ? entityUrn.Substring("urn:li:fs_conversation:".Length)
                    : entityUrn;

                var unreadCount = convElem.TryGetProperty("unreadCount", out var uc) ? uc.GetInt32() : 0;

                DateTime? lastActivityAt = null;
                if (convElem.TryGetProperty("lastActivityAt", out var lat))
                {
                    lastActivityAt = DateTimeOffset.FromUnixTimeMilliseconds(lat.GetInt64()).UtcDateTime;
                }

                // Extract participants
                var participants = new List<ParticipantDto>();
                if (convElem.TryGetProperty("participants", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in parts.EnumerateArray())
                    {
                        var pUrn = p.GetString();
                        if (!string.IsNullOrEmpty(pUrn))
                        {
                            if (messagingMembers.TryGetValue(pUrn, out var mpUrn) && miniProfiles.TryGetValue(mpUrn, out var part))
                            {
                                participants.Add(part);
                            }
                            else if (miniProfiles.TryGetValue(pUrn, out var partDirect))
                            {
                                participants.Add(partDirect);
                            }
                        }
                    }
                }

                // Title from participants or element
                var title = convElem.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(title) && participants.Count > 0)
                {
                    // Filter out current user if possible
                    var otherParticipants = participants
                        .Where(p => !string.IsNullOrEmpty(session.MemberUrn) && !string.Equals(p.ProfileUrn, session.MemberUrn, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    title = otherParticipants.Count > 0
                        ? string.Join(", ", otherParticipants.Select(p => p.Name).Where(nm => !string.IsNullOrWhiteSpace(nm)))
                        : participants.FirstOrDefault()?.Name;
                }

                // Extract last message
                MessagePreviewDto? lastMessage = null;
                if (convElem.TryGetProperty("events", out var eventsArray) && eventsArray.ValueKind == JsonValueKind.Array)
                {
                    var lastEventUrn = eventsArray.EnumerateArray().LastOrDefault().GetString();
                    if (!string.IsNullOrEmpty(lastEventUrn) && messageEvents.TryGetValue(lastEventUrn, out var eventItem))
                    {
                        lastMessage = ExtractMessagePreview(eventItem, miniProfiles, messagingMembers);
                    }
                }

                response.Conversations.Add(new ConversationDto
                {
                    Id = rawId,
                    EntityUrn = entityUrn,
                    Title = title ?? "LinkedIn Conversation",
                    UnreadCount = unreadCount,
                    LastActivityAt = lastActivityAt,
                    Participants = participants,
                    LastMessage = lastMessage
                });
            }

            response.Total = response.Conversations.Count;
        }
        catch
        {
            // Return whatever parsed
        }

        return response;
    }

    private static MessageListResponse ParseMessagesFromJson(string json, string conversationId, LinkedInSessionState session)
    {
        var response = new MessageListResponse
        {
            ConversationId = conversationId,
            Messages = new List<MessageDto>()
        };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Direct format check (from DOM extraction or clean response)
            if (root.TryGetProperty("messages", out var directMsgs) && directMsgs.ValueKind == JsonValueKind.Array)
            {
                var directList = JsonSerializer.Deserialize<List<MessageDto>>(directMsgs.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (directList != null)
                {
                    response.Messages = directList;
                    response.Total = directList.Count;
                    return response;
                }
            }

            var miniProfiles = new Dictionary<string, ParticipantDto>(StringComparer.OrdinalIgnoreCase);
            var messagingMembers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in included.EnumerateArray())
                {
                    if (item.TryGetProperty("$type", out var typeProp))
                    {
                        var typeName = typeProp.GetString() ?? string.Empty;

                        if (typeName.Contains("MiniProfile", StringComparison.OrdinalIgnoreCase))
                        {
                            var urn = item.TryGetProperty("entityUrn", out var u) ? u.GetString() : null;
                            if (!string.IsNullOrEmpty(urn))
                            {
                                var firstName = item.TryGetProperty("firstName", out var fn) ? fn.GetString() : string.Empty;
                                var lastName = item.TryGetProperty("lastName", out var ln) ? ln.GetString() : string.Empty;
                                miniProfiles[urn] = new ParticipantDto
                                {
                                    ProfileUrn = urn,
                                    Name = $"{firstName} {lastName}".Trim(),
                                    Headline = item.TryGetProperty("occupation", out var occ) ? occ.GetString() : null,
                                    PublicIdentifier = item.TryGetProperty("publicIdentifier", out var pub) ? pub.GetString() : null
                                };
                            }
                        }
                        else if (typeName.Contains("MessagingMember", StringComparison.OrdinalIgnoreCase))
                        {
                            var urn = item.TryGetProperty("entityUrn", out var u) ? u.GetString() : null;
                            var miniProfileUrn = item.TryGetProperty("miniProfile", out var mp) ? mp.GetString() : null;
                            if (!string.IsNullOrEmpty(urn) && !string.IsNullOrEmpty(miniProfileUrn))
                            {
                                messagingMembers[urn] = miniProfileUrn;
                            }
                        }
                    }
                }
            }

            var elements = new List<JsonElement>();
            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("elements", out var dElems) && dElems.ValueKind == JsonValueKind.Array)
            {
                elements.AddRange(dElems.EnumerateArray());
            }
            else if (root.TryGetProperty("elements", out var rElems) && rElems.ValueKind == JsonValueKind.Array)
            {
                elements.AddRange(rElems.EnumerateArray());
            }

            foreach (var elem in elements)
            {
                var id = elem.TryGetProperty("entityUrn", out var u) ? u.GetString() ?? string.Empty : string.Empty;

                DateTime? sentAt = null;
                if (elem.TryGetProperty("createdAt", out var ca))
                {
                    sentAt = DateTimeOffset.FromUnixTimeMilliseconds(ca.GetInt64()).UtcDateTime;
                }

                // Resolve sender
                var fromUrn = elem.TryGetProperty("from", out var f) ? f.GetString() : null;
                string? senderName = null;
                string? senderUrn = fromUrn;

                if (!string.IsNullOrEmpty(fromUrn))
                {
                    if (messagingMembers.TryGetValue(fromUrn, out var mpUrn) && miniProfiles.TryGetValue(mpUrn, out var part))
                    {
                        senderName = part.Name;
                        senderUrn = part.ProfileUrn;
                    }
                    else if (miniProfiles.TryGetValue(fromUrn, out var partDirect))
                    {
                        senderName = partDirect.Name;
                        senderUrn = partDirect.ProfileUrn;
                    }
                }

                var isFromMe = !string.IsNullOrEmpty(session.MemberUrn) &&
                               (string.Equals(senderUrn, session.MemberUrn, StringComparison.OrdinalIgnoreCase) ||
                                (senderName != null && string.Equals(senderName, session.Profile?.FullName, StringComparison.OrdinalIgnoreCase)));

                // Extract message body text
                var text = ExtractTextFromEvent(elem);

                response.Messages.Add(new MessageDto
                {
                    Id = id,
                    ConversationId = conversationId,
                    SenderName = senderName ?? "LinkedIn Member",
                    SenderUrn = senderUrn,
                    IsFromMe = isFromMe,
                    Text = text,
                    SentAt = sentAt
                });
            }

            // Order chronologically by default
            response.Messages = response.Messages.OrderBy(m => m.SentAt).ToList();
            response.Total = response.Messages.Count;
        }
        catch
        {
            // Return whatever parsed
        }

        return response;
    }

    private static MessagePreviewDto ExtractMessagePreview(
        JsonElement eventItem,
        Dictionary<string, ParticipantDto> miniProfiles,
        Dictionary<string, string> messagingMembers)
    {
        var id = eventItem.TryGetProperty("entityUrn", out var u) ? u.GetString() : null;
        DateTime? sentAt = null;
        if (eventItem.TryGetProperty("createdAt", out var ca))
        {
            sentAt = DateTimeOffset.FromUnixTimeMilliseconds(ca.GetInt64()).UtcDateTime;
        }

        var fromUrn = eventItem.TryGetProperty("from", out var f) ? f.GetString() : null;
        string? senderName = null;
        string? senderUrn = fromUrn;

        if (!string.IsNullOrEmpty(fromUrn))
        {
            if (messagingMembers.TryGetValue(fromUrn, out var mpUrn) && miniProfiles.TryGetValue(mpUrn, out var part))
            {
                senderName = part.Name;
                senderUrn = part.ProfileUrn;
            }
            else if (miniProfiles.TryGetValue(fromUrn, out var partDirect))
            {
                senderName = partDirect.Name;
                senderUrn = partDirect.ProfileUrn;
            }
        }

        var text = ExtractTextFromEvent(eventItem);

        return new MessagePreviewDto
        {
            Id = id,
            SenderName = senderName,
            SenderUrn = senderUrn,
            Text = text,
            SentAt = sentAt
        };
    }

    private static string? ExtractTextFromEvent(JsonElement elem)
    {
        if (elem.TryGetProperty("eventContent", out var ec))
        {
            if (ec.TryGetProperty("attributedBody", out var ab) && ab.TryGetProperty("text", out var t1))
            {
                return t1.GetString();
            }

            if (ec.TryGetProperty("customContent", out var cc) && cc.TryGetProperty("text", out var t2))
            {
                return t2.GetString();
            }

            if (ec.TryGetProperty("messageEvent", out var me))
            {
                if (me.TryGetProperty("attributedBody", out var ab2) && ab2.TryGetProperty("text", out var t3))
                {
                    return t3.GetString();
                }
                if (me.TryGetProperty("customContent", out var cc2) && cc2.TryGetProperty("text", out var t4))
                {
                    return t4.GetString();
                }
            }
        }

        if (elem.TryGetProperty("attributedBody", out var directAb) && directAb.TryGetProperty("text", out var directText))
        {
            return directText.GetString();
        }

        return null;
    }
}
