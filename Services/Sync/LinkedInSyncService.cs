using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LinkdUnified.Data;
using LinkdUnified.Data.Entities;
using LinkdUnified.Models;
using LinkdUnified.Services.Webhooks;

namespace LinkdUnified.Services.Sync;

public class LinkedInSyncService : ILinkedInSyncService
{
    private readonly AppDbContext _db;
    private readonly ILinkedInMessagingService _messagingService;
    private readonly ILinkedInSessionStore _sessionStore;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ILogger<LinkedInSyncService> _logger;

    public LinkedInSyncService(
        AppDbContext db,
        ILinkedInMessagingService messagingService,
        ILinkedInSessionStore sessionStore,
        IWebhookDispatcher webhookDispatcher,
        ILogger<LinkedInSyncService> logger)
    {
        _db = db;
        _messagingService = messagingService;
        _sessionStore = sessionStore;
        _webhookDispatcher = webhookDispatcher;
        _logger = logger;
    }

    public async Task<SyncResult> SyncAccountAsync(string? accountId = null, CancellationToken cancellationToken = default)
    {
        var session = _sessionStore.GetSession(accountId);
        if (session == null || !session.IsActive)
        {
            throw new UnauthorizedAccessException("No active LinkedIn account session available to sync.");
        }

        var result = new SyncResult
        {
            AccountId = session.AccountId
        };

        // Ensure account entity exists in DB
        var accountEntity = await _db.LinkedInAccounts.FindAsync(new object[] { session.AccountId }, cancellationToken);
        if (accountEntity == null)
        {
            accountEntity = new LinkedInAccountEntity
            {
                AccountId = session.AccountId,
                Username = session.Username,
                LiAtCookie = session.LiAtCookie,
                JsessionId = session.JsessionId,
                MemberUrn = session.MemberUrn,
                FullName = session.Profile?.FullName,
                Headline = session.Profile?.Headline,
                PublicIdentifier = session.Profile?.PublicIdentifier,
                ConnectedAt = session.CreatedAt,
                IsActive = true
            };
            _db.LinkedInAccounts.Add(accountEntity);
        }
        else
        {
            accountEntity.LiAtCookie = session.LiAtCookie;
            accountEntity.JsessionId = session.JsessionId;
            accountEntity.FullName = session.Profile?.FullName ?? accountEntity.FullName;
            accountEntity.Headline = session.Profile?.Headline ?? accountEntity.Headline;
            accountEntity.IsActive = true;
        }

        // 1. Fetch latest conversations from LinkedIn
        var liveConversations = await _messagingService.GetConversationsAsync(session.AccountId, cancellationToken);
        result.ConversationsChecked = liveConversations.Conversations.Count;

        foreach (var conv in liveConversations.Conversations)
        {
            var dbConv = await _db.Conversations.FindAsync(new object[] { conv.Id }, cancellationToken);
            var isNewConv = (dbConv == null);

            if (isNewConv)
            {
                dbConv = new ConversationEntity
                {
                    Id = conv.Id,
                    AccountId = session.AccountId,
                    EntityUrn = conv.EntityUrn,
                    Title = conv.Title,
                    UnreadCount = conv.UnreadCount,
                    LastActivityAt = conv.LastActivityAt,
                    ParticipantsJson = JsonSerializer.Serialize(conv.Participants),
                    LastMessageJson = conv.LastMessage != null ? JsonSerializer.Serialize(conv.LastMessage) : null,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.Conversations.Add(dbConv);
                await _db.SaveChangesAsync(cancellationToken);
            }
            else if (dbConv != null)
            {
                dbConv.Title = conv.Title;
                dbConv.UnreadCount = conv.UnreadCount;
                dbConv.LastActivityAt = conv.LastActivityAt;
                dbConv.ParticipantsJson = JsonSerializer.Serialize(conv.Participants);
                dbConv.LastMessageJson = conv.LastMessage != null ? JsonSerializer.Serialize(conv.LastMessage) : null;
                dbConv.UpdatedAt = DateTime.UtcNow;
            }

            // 2. Fetch messages for this conversation to check for new messages
            try
            {
                var liveMessages = await _messagingService.GetMessagesAsync(conv.Id, session.AccountId, cancellationToken);

                // Get existing message IDs in DB for this conversation
                var existingMessageIds = await _db.Messages
                    .Where(m => m.ConversationId == conv.Id)
                    .Select(m => m.Id)
                    .ToListAsync(cancellationToken);

                var existingIdSet = new HashSet<string>(existingMessageIds, StringComparer.OrdinalIgnoreCase);

                foreach (var msg in liveMessages.Messages)
                {
                    if (!existingIdSet.Contains(msg.Id))
                    {
                        var messageEntity = new MessageEntity
                        {
                            Id = msg.Id,
                            ConversationId = conv.Id,
                            SenderName = msg.SenderName,
                            SenderUrn = msg.SenderUrn,
                            IsFromMe = msg.IsFromMe,
                            Text = msg.Text,
                            SentAt = msg.SentAt,
                            ReceivedAt = DateTime.UtcNow
                        };

                        _db.Messages.Add(messageEntity);
                        existingIdSet.Add(msg.Id);
                        result.NewMessagesCount++;

                        var webhookEvent = new WebhookMessageEvent
                        {
                            Event = "message.received",
                            Timestamp = DateTime.UtcNow,
                            AccountId = session.AccountId,
                            ConversationId = conv.Id,
                            ConversationTitle = conv.Title,
                            SenderName = msg.SenderName,
                            SenderUrn = msg.SenderUrn,
                            IsFromMe = msg.IsFromMe,
                            Text = msg.Text,
                            SentAt = msg.SentAt,
                            MessageId = msg.Id
                        };

                        result.NewMessagesDispatched.Add(webhookEvent);

                        // Trigger Webhook dispatch
                        await _webhookDispatcher.DispatchMessageReceivedAsync(webhookEvent, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync messages for conversation {ConversationId}", conv.Id);
            }
        }

        accountEntity.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Sync completed for account {AccountId}. Checked {ConvCount} conversations, found {NewMsgCount} new messages.",
            session.AccountId, result.ConversationsChecked, result.NewMessagesCount);

        return result;
    }

    public async Task<ConversationListResponse> GetSyncedConversationsAsync(string? accountId = null, CancellationToken cancellationToken = default)
    {
        var session = _sessionStore.GetSession(accountId);
        var targetAccountId = accountId ?? session?.AccountId;

        var query = _db.Conversations.AsQueryable();
        if (!string.IsNullOrEmpty(targetAccountId))
        {
            query = query.Where(c => c.AccountId == targetAccountId);
        }

        var dbConversations = await query
            .OrderByDescending(c => c.LastActivityAt ?? c.UpdatedAt)
            .ToListAsync(cancellationToken);

        var list = new List<ConversationDto>();
        foreach (var entity in dbConversations)
        {
            var participants = !string.IsNullOrEmpty(entity.ParticipantsJson)
                ? JsonSerializer.Deserialize<List<ParticipantDto>>(entity.ParticipantsJson) ?? new()
                : new();

            var lastMessage = !string.IsNullOrEmpty(entity.LastMessageJson)
                ? JsonSerializer.Deserialize<MessagePreviewDto>(entity.LastMessageJson)
                : null;

            list.Add(new ConversationDto
            {
                Id = entity.Id,
                EntityUrn = entity.EntityUrn,
                Title = entity.Title,
                UnreadCount = entity.UnreadCount,
                LastActivityAt = entity.LastActivityAt,
                Participants = participants,
                LastMessage = lastMessage
            });
        }

        return new ConversationListResponse
        {
            AccountId = targetAccountId ?? string.Empty,
            Total = list.Count,
            Conversations = list
        };
    }

    public async Task<MessageListResponse> GetSyncedMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var messages = await _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.SentAt ?? m.ReceivedAt)
            .ToListAsync(cancellationToken);

        return new MessageListResponse
        {
            ConversationId = conversationId,
            Total = messages.Count,
            Messages = messages.Select(m => new MessageDto
            {
                Id = m.Id,
                ConversationId = m.ConversationId,
                SenderName = m.SenderName,
                SenderUrn = m.SenderUrn,
                IsFromMe = m.IsFromMe,
                Text = m.Text,
                SentAt = m.SentAt
            }).ToList()
        };
    }
}
