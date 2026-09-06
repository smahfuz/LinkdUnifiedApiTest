using Microsoft.AspNetCore.Mvc;
using LinkdUnified.Models;
using LinkdUnified.Services;

namespace LinkdUnified.Controllers;

[ApiController]
[Route("api/linkedin")]
[Produces("application/json")]
public class LinkedInController : ControllerBase
{
    private readonly ILinkedInAuthService _authService;
    private readonly ILinkedInMessagingService _messagingService;
    private readonly ILogger<LinkedInController> _logger;

    public LinkedInController(
        ILinkedInAuthService authService,
        ILinkedInMessagingService messagingService,
        ILogger<LinkedInController> logger)
    {
        _authService = authService;
        _messagingService = messagingService;
        _logger = logger;
    }

    /// <summary>
    /// Connects a LinkedIn account using login credentials (or session cookie).
    /// If 2FA / checkpoint is triggered, returns a challengeToken to submit the email/SMS code.
    /// </summary>
    [HttpPost("accounts/connect")]
    [ProducesResponseType(typeof(ApiResponse<ConnectAccountResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ConnectAccount([FromBody] ConnectAccountRequest request, CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_REQUEST", "Request body cannot be empty."));
        }

        try
        {
            var result = await _authService.ConnectAccountAsync(request, cancellationToken);
            return Ok(ApiResponse<ConnectAccountResult>.Ok(result, result.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_ARGUMENTS", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.Fail("AUTH_FAILED", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error connecting LinkedIn account");
            var errorResponse = ApiResponse<object>.Fail("INTERNAL_ERROR", ex.Message);
            errorResponse.Error!.Details = ex.InnerException?.Message ?? ex.StackTrace;
            return StatusCode(StatusCodes.Status500InternalServerError, errorResponse);
        }
    }

    /// <summary>
    /// Submits the 2FA / Email verification PIN code received after initiating connection.
    /// </summary>
    [HttpPost("accounts/challenge")]
    [HttpPost("accounts/checkpoint")]
    [ProducesResponseType(typeof(ApiResponse<ConnectAccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SubmitChallenge([FromBody] SubmitChallengeRequest request, CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_REQUEST", "Request body cannot be empty."));
        }

        try
        {
            var result = await _authService.SubmitChallengeAsync(request, cancellationToken);
            return Ok(ApiResponse<ConnectAccountResponse>.Ok(result, "Verification successful. LinkedIn account connected."));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_ARGUMENTS", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.Fail("VERIFICATION_FAILED", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error submitting challenge code");
            return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("INTERNAL_ERROR", "An error occurred during verification."));
        }
    }

    /// <summary>
    /// Requests LinkedIn to resend the verification PIN code to email/phone.
    /// </summary>
    [HttpPost("accounts/challenge/resend")]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResendChallenge([FromBody] ResendChallengeRequest request, CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_REQUEST", "Request body cannot be empty."));
        }

        try
        {
            var message = await _authService.ResendChallengeCodeAsync(request, cancellationToken);
            return Ok(ApiResponse<string>.Ok(message, message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_ARGUMENTS", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error resending challenge code");
            return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("INTERNAL_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Gets conversations for the connected LinkedIn account.
    /// </summary>
    [HttpGet("conversations")]
    [ProducesResponseType(typeof(ApiResponse<ConversationListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConversations(
        [FromQuery] string? accountId = null,
        [FromHeader(Name = "X-Account-Id")] string? headerAccountId = null,
        CancellationToken cancellationToken = default)
    {
        var targetAccountId = !string.IsNullOrWhiteSpace(accountId) ? accountId : headerAccountId;

        try
        {
            var conversations = await _messagingService.GetConversationsAsync(targetAccountId, cancellationToken);
            return Ok(ApiResponse<ConversationListResponse>.Ok(conversations));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.Fail("UNAUTHORIZED", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching LinkedIn conversations");
            return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("FETCH_CONVERSATIONS_FAILED", ex.Message));
        }
    }

    /// <summary>
    /// Gets messages for a specific LinkedIn conversation.
    /// </summary>
    [HttpGet("conversations/{conversationId}/messages")]
    [ProducesResponseType(typeof(ApiResponse<MessageListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMessages(
        [FromRoute] string conversationId,
        [FromQuery] string? accountId = null,
        [FromHeader(Name = "X-Account-Id")] string? headerAccountId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_CONVERSATION_ID", "Conversation ID must be specified."));
        }

        var targetAccountId = !string.IsNullOrWhiteSpace(accountId) ? accountId : headerAccountId;

        try
        {
            var messages = await _messagingService.GetMessagesAsync(conversationId, targetAccountId, cancellationToken);
            return Ok(ApiResponse<MessageListResponse>.Ok(messages));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.Fail("UNAUTHORIZED", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching messages for conversation {ConversationId}", conversationId);
            return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("FETCH_MESSAGES_FAILED", ex.Message));
        }
    }

    /// <summary>
    /// Instant Live Messages Feed: Fetches latest incoming and outgoing messages directly from LinkedIn in real-time (bypassing DB, no accountId needed).
    /// </summary>
    [HttpGet("messages/live")]
    [ProducesResponseType(typeof(ApiResponse<List<object>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetLiveMessages(
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var conversations = await _messagingService.GetConversationsAsync(null, cancellationToken);
            var liveMessages = conversations.Conversations
                .Where(c => c.LastMessage != null && !string.IsNullOrWhiteSpace(c.LastMessage.Text))
                .OrderByDescending(c => c.LastMessage?.SentAt ?? c.LastActivityAt)
                .Take(limit)
                .Select(c => new
                {
                    conversationId = c.Id,
                    conversationTitle = c.Title,
                    messageId = c.LastMessage?.Id,
                    senderName = c.LastMessage?.SenderName,
                    senderUrn = c.LastMessage?.SenderUrn,
                    text = c.LastMessage?.Text,
                    sentAt = c.LastMessage?.SentAt,
                    unreadCount = c.UnreadCount
                })
                .ToList();

            return Ok(ApiResponse<object>.Ok(liveMessages, "Live messages retrieved directly from LinkedIn."));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.Fail("UNAUTHORIZED", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching live messages from LinkedIn");
            return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("FETCH_LIVE_MESSAGES_FAILED", ex.Message));
        }
    }
}
