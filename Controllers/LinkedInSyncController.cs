using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LinkdUnified.Data;
using LinkdUnified.Models;
using LinkdUnified.Services.Sync;

namespace LinkdUnified.Controllers;

[ApiController]
[Route("api/linkedin")]
[Produces("application/json")]
public class LinkedInSyncController : ControllerBase
{
    private readonly ILinkedInSyncService _syncService;
    private readonly AppDbContext _db;
    private readonly ILogger<LinkedInSyncController> _logger;

    public LinkedInSyncController(
        ILinkedInSyncService syncService,
        AppDbContext db,
        ILogger<LinkedInSyncController> logger)
    {
        _syncService = syncService;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Triggers immediate synchronization of LinkedIn conversations and messages to SQL Server DB and dispatches webhooks.
    /// </summary>
    [HttpPost("sync")]
    [ProducesResponseType(typeof(ApiResponse<SyncResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> TriggerSync([FromQuery] string? accountId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _syncService.SyncAccountAsync(accountId, cancellationToken);
            return Ok(ApiResponse<SyncResult>.Ok(result, $"Sync complete. {result.NewMessagesCount} new messages detected."));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, ApiResponse<object>.Fail("UNAUTHORIZED", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing LinkedIn account");
            return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("SYNC_FAILED", ex.Message));
        }
    }

    /// <summary>
    /// Gets all conversations stored in SQL Server Database.
    /// </summary>
    [HttpGet("db/conversations")]
    [ProducesResponseType(typeof(ApiResponse<ConversationListResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDbConversations([FromQuery] string? accountId = null, CancellationToken cancellationToken = default)
    {
        var response = await _syncService.GetSyncedConversationsAsync(accountId, cancellationToken);
        return Ok(ApiResponse<ConversationListResponse>.Ok(response));
    }

    /// <summary>
    /// Gets all messages for a conversation stored in SQL Server Database.
    /// </summary>
    [HttpGet("db/conversations/{conversationId}/messages")]
    [ProducesResponseType(typeof(ApiResponse<MessageListResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDbMessages([FromRoute] string conversationId, CancellationToken cancellationToken = default)
    {
        var response = await _syncService.GetSyncedMessagesAsync(conversationId, cancellationToken);
        return Ok(ApiResponse<MessageListResponse>.Ok(response));
    }

    /// <summary>
    /// Instant Live View: Gets the most recent incoming messages with sender name and text across all conversations.
    /// </summary>
    [HttpGet("db/messages/recent")]
    [ProducesResponseType(typeof(ApiResponse<List<object>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentIncomingMessages([FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        var recentMessages = await _db.Messages
            .Include(m => m.Conversation)
            .OrderByDescending(m => m.SentAt ?? m.ReceivedAt)
            .Take(limit)
            .Select(m => new
            {
                messageId = m.Id,
                conversationId = m.ConversationId,
                conversationTitle = m.Conversation != null ? m.Conversation.Title : null,
                senderName = m.SenderName,
                senderUrn = m.SenderUrn,
                isFromMe = m.IsFromMe,
                text = m.Text,
                sentAt = m.SentAt,
                receivedAt = m.ReceivedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse<object>.Ok(recentMessages, "Recent messages retrieved from database."));
    }
}
