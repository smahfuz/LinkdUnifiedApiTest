using LinkdUnified.Models;

namespace LinkdUnified.Services.Browser;

public interface ILinkedInBrowserAuthService
{
    Task<ConnectAccountResult> LoginWithBrowserAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<ConnectAccountResponse> SubmitBrowserChallengeAsync(string challengeToken, string pinCode, CancellationToken cancellationToken = default);
    Task<string> ResendBrowserChallengeAsync(string challengeToken, CancellationToken cancellationToken = default);
    Task<string> FetchWithBrowserAsync(string url, LinkedInSessionState session, CancellationToken cancellationToken = default);
}
