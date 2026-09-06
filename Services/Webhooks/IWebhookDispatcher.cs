using LinkdUnified.Models;

namespace LinkdUnified.Services.Webhooks;

public interface IWebhookDispatcher
{
    Task DispatchMessageReceivedAsync(WebhookMessageEvent messageEvent, CancellationToken cancellationToken = default);
}
