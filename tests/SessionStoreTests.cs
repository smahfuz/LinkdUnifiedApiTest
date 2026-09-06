using LinkdUnified.Models;
using LinkdUnified.Services;
using Xunit;

namespace LinkdUnified.Tests;

public class SessionStoreTests
{
    [Fact]
    public void SaveSession_And_GetSession_ReturnsCorrectSession()
    {
        var store = new InMemoryLinkedInSessionStore();
        var session = new LinkedInSessionState
        {
            AccountId = "acc_001",
            Username = "user@example.com",
            LiAtCookie = "cookie_value_123",
            JsessionId = "\"ajax:12345\""
        };

        store.SaveSession(session);

        var retrieved = store.GetSession("acc_001");
        Assert.NotNull(retrieved);
        Assert.Equal("acc_001", retrieved.AccountId);
        Assert.Equal("cookie_value_123", retrieved.LiAtCookie);
        Assert.Equal("ajax:12345", retrieved.GetCsrfToken());
    }

    [Fact]
    public void GetSession_WithNullAccountId_ReturnsDefaultActiveSession()
    {
        var store = new InMemoryLinkedInSessionStore();
        var session = new LinkedInSessionState
        {
            AccountId = "acc_002",
            Username = "user2@example.com",
            LiAtCookie = "cookie_value_456",
            JsessionId = "\"ajax:67890\""
        };

        store.SaveSession(session);

        var retrieved = store.GetSession(null);
        Assert.NotNull(retrieved);
        Assert.Equal("acc_002", retrieved.AccountId);
    }

    [Fact]
    public void InvalidateSession_MakesSessionInactive()
    {
        var store = new InMemoryLinkedInSessionStore();
        var session = new LinkedInSessionState
        {
            AccountId = "acc_003",
            Username = "user3@example.com",
            LiAtCookie = "cookie_value_789"
        };

        store.SaveSession(session);
        store.InvalidateSession("acc_003");

        var retrieved = store.GetSession("acc_003");
        Assert.Null(retrieved);
    }
}
