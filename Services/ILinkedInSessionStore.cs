using LinkdUnified.Models;

namespace LinkdUnified.Services;

public interface ILinkedInSessionStore
{
    void SaveSession(LinkedInSessionState session);
    LinkedInSessionState? GetSession(string? accountId = null);
    IEnumerable<LinkedInSessionState> GetAllSessions();
    bool RemoveSession(string accountId);
    void InvalidateSession(string accountId);

    void SavePendingChallenge(PendingChallengeState challenge);
    PendingChallengeState? GetPendingChallenge(string challengeToken);
    bool RemovePendingChallenge(string challengeToken);
}
