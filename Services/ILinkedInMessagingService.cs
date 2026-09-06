using LinkdUnified.Models;

namespace LinkdUnified.Services;

public interface ILinkedInMessagingService
{
    Task<ConversationListResponse> GetConversationsAsync(string? accountId = null, CancellationToken cancellationToken = default);
    Task<MessageListResponse> GetMessagesAsync(string conversationId, string? accountId = null, CancellationToken cancellationToken = default);
}
