using LinkdUnified.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkdUnified.Services.Sync;

public class LinkedInMessagePollerWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LinkedInMessagePollerWorker> _logger;

    public LinkedInMessagePollerWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<LinkedInMessagePollerWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _configuration.GetValue<int>("LinkedInSync:PollingIntervalSeconds", 15);
        var isEnabled = _configuration.GetValue<bool>("LinkedInSync:AutoSyncEnabled", true);

        if (!isEnabled)
        {
            _logger.LogInformation("LinkedIn Auto-Sync background worker is disabled via configuration.");
            return;
        }

        _logger.LogInformation("LinkedIn Real-Time Message Poller Worker started (Interval: {Interval}s).", intervalSeconds);

        // Initial delay to allow application startup
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var sessionStore = scope.ServiceProvider.GetRequiredService<ILinkedInSessionStore>();
                var syncService = scope.ServiceProvider.GetRequiredService<ILinkedInSyncService>();

                var activeSessions = sessionStore.GetAllSessions().ToList();
                foreach (var session in activeSessions)
                {
                    try
                    {
                        var syncResult = await syncService.SyncAccountAsync(session.AccountId, stoppingToken);
                        if (syncResult.NewMessagesCount > 0)
                        {
                            _logger.LogInformation("Real-time worker detected {Count} new messages for account {AccountId}",
                                syncResult.NewMessagesCount, session.AccountId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Background poll failed for account {AccountId}", session.AccountId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LinkedIn real-time message poller loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }
}
