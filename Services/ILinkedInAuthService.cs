using LinkdUnified.Models;

namespace LinkdUnified.Services;

public interface ILinkedInAuthService
{
    Task<ConnectAccountResult> ConnectAccountAsync(ConnectAccountRequest request, CancellationToken cancellationToken = default);
    Task<ConnectAccountResponse> SubmitChallengeAsync(SubmitChallengeRequest request, CancellationToken cancellationToken = default);
    Task<string> ResendChallengeCodeAsync(ResendChallengeRequest request, CancellationToken cancellationToken = default);
}
