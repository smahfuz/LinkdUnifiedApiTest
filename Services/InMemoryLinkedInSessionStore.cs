using System.Collections.Concurrent;
using LinkdUnified.Models;

namespace LinkdUnified.Services;

public class InMemoryLinkedInSessionStore : ILinkedInSessionStore
{
    private readonly ConcurrentDictionary<string, LinkedInSessionState> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingChallengeState> _pendingChallenges = new();
    private string? _lastConnectedAccountId;

    public void SaveSession(LinkedInSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.AccountId] = session;
        _lastConnectedAccountId = session.AccountId;
    }

    public LinkedInSessionState? GetSession(string? accountId = null)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            if (_lastConnectedAccountId != null && _sessions.TryGetValue(_lastConnectedAccountId, out var defaultSession))
            {
                if (defaultSession.IsActive) return defaultSession;
            }

            // Fallback to any active session
            return _sessions.Values.FirstOrDefault(s => s.IsActive);
        }

        if (_sessions.TryGetValue(accountId, out var session) && session.IsActive)
        {
            return session;
        }

        return null;
    }

    public IEnumerable<LinkedInSessionState> GetAllSessions()
    {
        return _sessions.Values.Where(s => s.IsActive);
    }

    public bool RemoveSession(string accountId)
    {
        return _sessions.TryRemove(accountId, out _);
    }

    public void InvalidateSession(string accountId)
    {
        if (_sessions.TryGetValue(accountId, out var session))
        {
            session.IsActive = false;
        }
    }

    public void SavePendingChallenge(PendingChallengeState challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        _pendingChallenges[challenge.ChallengeToken] = challenge;
    }

    public PendingChallengeState? GetPendingChallenge(string challengeToken)
    {
        if (string.IsNullOrWhiteSpace(challengeToken)) return null;

        // Clean expired challenges (> 15 minutes)
        foreach (var kvp in _pendingChallenges)
        {
            if (DateTime.UtcNow - kvp.Value.CreatedAt > TimeSpan.FromMinutes(15))
            {
                _pendingChallenges.TryRemove(kvp.Key, out _);
            }
        }

        return _pendingChallenges.TryGetValue(challengeToken, out var state) ? state : null;
    }

    public bool RemovePendingChallenge(string challengeToken)
    {
        if (string.IsNullOrWhiteSpace(challengeToken)) return false;
        return _pendingChallenges.TryRemove(challengeToken, out _);
    }
}
