using LinkdUnified.Models;

namespace LinkdUnified.Services.Sync;

public class SyncResult
{
    public string AccountId { get; set; } = string.Empty;
    public int ConversationsChecked { get; set; }
    public int NewMessagesCount { get; set; }
    public List<WebhookMessageEvent> NewMessagesDispatched { get; set; } = new();
}

public interface ILinkedInSyncService
{
    Task<SyncResult> SyncAccountAsync(string? accountId = null, CancellationToken cancellationToken = default);
    Task<ConversationListResponse> GetSyncedConversationsAsync(string? accountId = null, CancellationToken cancellationToken = default);
    Task<MessageListResponse> GetSyncedMessagesAsync(string conversationId, CancellationToken cancellationToken = default);
}
