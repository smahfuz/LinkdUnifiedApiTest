using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LinkdUnified.Data;
using LinkdUnified.Data.Entities;
using LinkdUnified.Models;

namespace LinkdUnified.Controllers;

[ApiController]
[Route("api/linkedin/webhooks")]
[Produces("application/json")]
public class WebhookController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(AppDbContext db, ILogger<WebhookController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Subscribes a webhook URL to receive real-time LinkedIn incoming message events.
    /// </summary>
    [HttpPost("subscribe")]
    [ProducesResponseType(typeof(ApiResponse<WebhookSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubscribeWebhook([FromBody] CreateWebhookRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.TargetUrl))
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_URL", "Target URL is required."));
        }

        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_URL", "A valid HTTP or HTTPS absolute URL is required."));
        }

        var subscription = new WebhookSubscriptionEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            TargetUrl = request.TargetUrl.Trim(),
            Secret = request.Secret?.Trim(),
            AccountId = request.AccountId?.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.WebhookSubscriptions.Add(subscription);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registered webhook subscription {Id} for URL {TargetUrl}", subscription.Id, subscription.TargetUrl);

        var dto = new WebhookSubscriptionDto
        {
            Id = subscription.Id,
            TargetUrl = subscription.TargetUrl,
            AccountId = subscription.AccountId,
            IsActive = subscription.IsActive,
            CreatedAt = subscription.CreatedAt
        };

        return Ok(ApiResponse<WebhookSubscriptionDto>.Ok(dto, "Webhook subscribed successfully."));
    }

    /// <summary>
    /// Lists all registered webhook subscriptions.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<WebhookSubscriptionDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWebhooks(CancellationToken cancellationToken)
    {
        var subscriptions = await _db.WebhookSubscriptions
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new WebhookSubscriptionDto
            {
                Id = w.Id,
                TargetUrl = w.TargetUrl,
                AccountId = w.AccountId,
                IsActive = w.IsActive,
                CreatedAt = w.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse<List<WebhookSubscriptionDto>>.Ok(subscriptions));
    }

    /// <summary>
    /// Deletes a webhook subscription by ID.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteWebhook([FromRoute] string id, CancellationToken cancellationToken)
    {
        var subscription = await _db.WebhookSubscriptions.FindAsync(new object[] { id }, cancellationToken);
        if (subscription == null)
        {
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Webhook subscription not found."));
        }

        _db.WebhookSubscriptions.Remove(subscription);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { deleted = true }, "Webhook unsubscribed successfully."));
    }

    /// <summary>
    /// Gets recent webhook delivery logs.
    /// </summary>
    [HttpGet("logs")]
    [ProducesResponseType(typeof(ApiResponse<List<object>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWebhookLogs([FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var logs = await _db.WebhookEventLogs
            .OrderByDescending(l => l.SentAt)
            .Take(limit)
            .Select(l => new
            {
                l.Id,
                l.WebhookSubscriptionId,
                l.EventType,
                l.StatusCode,
                l.IsSuccess,
                l.ResponseBody,
                l.SentAt
            })
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse<object>.Ok(logs));
    }
}
