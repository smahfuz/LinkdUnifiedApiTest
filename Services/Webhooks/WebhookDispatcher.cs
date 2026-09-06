using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LinkdUnified.Data;
using LinkdUnified.Data.Entities;
using LinkdUnified.Models;

namespace LinkdUnified.Services.Webhooks;

public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDispatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task DispatchMessageReceivedAsync(WebhookMessageEvent messageEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageEvent);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Find active webhooks for this account (or global webhooks without accountId)
        var subscriptions = await db.WebhookSubscriptions
            .Where(w => w.IsActive && (w.AccountId == null || w.AccountId == messageEvent.AccountId))
            .ToListAsync(cancellationToken);

        if (subscriptions.Count == 0)
        {
            _logger.LogDebug("No active webhook subscriptions found for account {AccountId}", messageEvent.AccountId);
            return;
        }

        var payloadJson = JsonSerializer.Serialize(messageEvent);
        var httpClient = _httpClientFactory.CreateClient("WebhookClient");

        foreach (var sub in subscriptions)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, sub.TargetUrl)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };

                request.Headers.Add("X-Webhook-Event", messageEvent.Event);
                request.Headers.Add("X-Webhook-Delivery", Guid.NewGuid().ToString("N"));

                if (!string.IsNullOrEmpty(sub.Secret))
                {
                    var signature = ComputeHmacSha256(payloadJson, sub.Secret);
                    request.Headers.Add("X-Webhook-Signature", signature);
                }

                _logger.LogInformation("Dispatching webhook event {Event} to {TargetUrl}", messageEvent.Event, sub.TargetUrl);

                var response = await httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                var log = new WebhookEventLogEntity
                {
                    WebhookSubscriptionId = sub.Id,
                    EventType = messageEvent.Event,
                    Payload = payloadJson,
                    StatusCode = (int)response.StatusCode,
                    IsSuccess = response.IsSuccessStatusCode,
                    ResponseBody = responseBody.Length > 2000 ? responseBody.Substring(0, 2000) : responseBody,
                    SentAt = DateTime.UtcNow
                };

                db.WebhookEventLogs.Add(log);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver webhook to {TargetUrl}", sub.TargetUrl);

                var log = new WebhookEventLogEntity
                {
                    WebhookSubscriptionId = sub.Id,
                    EventType = messageEvent.Event,
                    Payload = payloadJson,
                    StatusCode = 0,
                    IsSuccess = false,
                    ResponseBody = ex.Message,
                    SentAt = DateTime.UtcNow
                };

                db.WebhookEventLogs.Add(log);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string ComputeHmacSha256(string data, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(dataBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
