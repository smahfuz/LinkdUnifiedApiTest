using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using LinkdUnified.Controllers;
using LinkdUnified.Models;
using LinkdUnified.Services;
using Xunit;

namespace LinkdUnified.Tests;

public class LinkedInControllerTests
{
    private class FakeLinkedInAuthService : ILinkedInAuthService
    {
        public bool ShouldFailAuth { get; set; }
        public bool ShouldChallenge { get; set; }
        public bool ShouldFailChallengeVerification { get; set; }

        public Task<ConnectAccountResult> ConnectAccountAsync(ConnectAccountRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.Username) && string.IsNullOrWhiteSpace(request.SessionCookie))
            {
                throw new ArgumentException("Credentials required.");
            }

            if (ShouldFailAuth)
            {
                throw new UnauthorizedAccessException("LinkedIn authentication failed: Invalid username or password.");
            }

            if (ShouldChallenge)
            {
                return Task.FromResult(new ConnectAccountResult
                {
                    Status = "checkpoint_required",
                    RequiresChallenge = true,
                    ChallengeToken = "chk_test123",
                    ChallengeType = "EMAIL_PIN",
                    Message = "A verification code has been sent to your email. Submit code to complete login."
                });
            }

            return Task.FromResult(new ConnectAccountResult
            {
                Status = "connected",
                RequiresChallenge = false,
                Account = new ConnectAccountResponse
                {
                    AccountId = "urn:li:member:12345678",
                    Status = "connected",
                    ConnectedAt = DateTime.UtcNow,
                    Profile = new ConnectedProfileInfo
                    {
                        MemberUrn = "urn:li:member:12345678",
                        FirstName = "Alex",
                        LastName = "Doe",
                        FullName = "Alex Doe",
                        Headline = "Software Engineer"
                    }
                }
            });
        }

        public Task<ConnectAccountResponse> SubmitChallengeAsync(SubmitChallengeRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.ChallengeToken) || string.IsNullOrWhiteSpace(request.Code))
            {
                throw new ArgumentException("Challenge token and code are required.");
            }

            if (ShouldFailChallengeVerification)
            {
                throw new UnauthorizedAccessException("Verification failed. The code entered may be incorrect or expired.");
            }

            return Task.FromResult(new ConnectAccountResponse
            {
                AccountId = "urn:li:member:12345678",
                Status = "connected",
                ConnectedAt = DateTime.UtcNow,
                Profile = new ConnectedProfileInfo
                {
                    MemberUrn = "urn:li:member:12345678",
                    FirstName = "Alex",
                    LastName = "Doe",
                    FullName = "Alex Doe",
                    Headline = "Software Engineer"
                }
            });
        }

        public Task<string> ResendChallengeCodeAsync(ResendChallengeRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(request.ChallengeToken))
            {
                throw new ArgumentException("Challenge token required.");
            }

            return Task.FromResult("Verification code has been resent to your email.");
        }
    }

    private class FakeLinkedInMessagingService : ILinkedInMessagingService
    {
        public bool ShouldFailUnauthorized { get; set; }

        public Task<ConversationListResponse> GetConversationsAsync(string? accountId = null, CancellationToken cancellationToken = default)
        {
            if (ShouldFailUnauthorized)
            {
                throw new UnauthorizedAccessException("Session expired.");
            }

            return Task.FromResult(new ConversationListResponse
            {
                AccountId = accountId ?? "urn:li:member:12345678",
                Total = 1,
                Conversations = new List<ConversationDto>
                {
                    new()
                    {
                        Id = "2-conv123",
                        EntityUrn = "urn:li:fs_conversation:2-conv123",
                        Title = "Sarah Connor",
                        UnreadCount = 1,
                        LastActivityAt = DateTime.UtcNow,
                        Participants = new List<ParticipantDto>
                        {
                            new() { Name = "Sarah Connor", ProfileUrn = "urn:li:fs_miniProfile:sarah" }
                        },
                        LastMessage = new MessagePreviewDto
                        {
                            SenderName = "Sarah Connor",
                            Text = "Hello! Are you available tomorrow?",
                            SentAt = DateTime.UtcNow
                        }
                    }
                }
            });
        }

        public Task<MessageListResponse> GetMessagesAsync(string conversationId, string? accountId = null, CancellationToken cancellationToken = default)
        {
            if (ShouldFailUnauthorized)
            {
                throw new UnauthorizedAccessException("Session expired.");
            }

            return Task.FromResult(new MessageListResponse
            {
                ConversationId = conversationId,
                Total = 2,
                Messages = new List<MessageDto>
                {
                    new()
                    {
                        Id = "urn:li:fs_event:msg1",
                        ConversationId = conversationId,
                        SenderName = "Sarah Connor",
                        SenderUrn = "urn:li:fs_miniProfile:sarah",
                        IsFromMe = false,
                        Text = "Hello! Are you available tomorrow?",
                        SentAt = DateTime.UtcNow.AddMinutes(-5)
                    },
                    new()
                    {
                        Id = "urn:li:fs_event:msg2",
                        ConversationId = conversationId,
                        SenderName = "Alex Doe",
                        SenderUrn = "urn:li:member:12345678",
                        IsFromMe = true,
                        Text = "Yes, let's schedule a call.",
                        SentAt = DateTime.UtcNow
                    }
                }
            });
        }
    }

    [Fact]
    public async Task ConnectAccount_WithValidCredentials_ReturnsOkResponse()
    {
        var authService = new FakeLinkedInAuthService();
        var messagingService = new FakeLinkedInMessagingService();
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var request = new ConnectAccountRequest
        {
            Username = "alex.doe@example.com",
            Password = "SecurePassword123!"
        };

        var result = await controller.ConnectAccount(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<ConnectAccountResult>>(okResult.Value);
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.False(response.Data.RequiresChallenge);
        Assert.Equal("urn:li:member:12345678", response.Data.Account?.AccountId);
    }

    [Fact]
    public async Task ConnectAccount_WhenChallengeRequired_ReturnsChallengeToken()
    {
        var authService = new FakeLinkedInAuthService { ShouldChallenge = true };
        var messagingService = new FakeLinkedInMessagingService();
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var request = new ConnectAccountRequest
        {
            Username = "alex.doe@example.com",
            Password = "SecurePassword123!"
        };

        var result = await controller.ConnectAccount(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<ConnectAccountResult>>(okResult.Value);
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.True(response.Data.RequiresChallenge);
        Assert.Equal("chk_test123", response.Data.ChallengeToken);
        Assert.Equal("EMAIL_PIN", response.Data.ChallengeType);
    }

    [Fact]
    public async Task SubmitChallenge_WithValidCode_ConnectsAccount()
    {
        var authService = new FakeLinkedInAuthService();
        var messagingService = new FakeLinkedInMessagingService();
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var request = new SubmitChallengeRequest
        {
            ChallengeToken = "chk_test123",
            Code = "654321"
        };

        var result = await controller.SubmitChallenge(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<ConnectAccountResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal("urn:li:member:12345678", response.Data.AccountId);
    }

    [Fact]
    public async Task SubmitChallenge_WithInvalidCode_ReturnsUnauthorized()
    {
        var authService = new FakeLinkedInAuthService { ShouldFailChallengeVerification = true };
        var messagingService = new FakeLinkedInMessagingService();
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var request = new SubmitChallengeRequest
        {
            ChallengeToken = "chk_test123",
            Code = "000000"
        };

        var result = await controller.SubmitChallenge(request, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(401, statusResult.StatusCode);
        var response = Assert.IsType<ApiResponse<object>>(statusResult.Value);
        Assert.False(response.Success);
        Assert.Equal("VERIFICATION_FAILED", response.Error?.Code);
    }

    [Fact]
    public async Task ConnectAccount_WithInvalidCredentials_ReturnsUnauthorized()
    {
        var authService = new FakeLinkedInAuthService { ShouldFailAuth = true };
        var messagingService = new FakeLinkedInMessagingService();
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var request = new ConnectAccountRequest
        {
            Username = "alex.doe@example.com",
            Password = "WrongPassword"
        };

        var result = await controller.ConnectAccount(request, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(401, statusResult.StatusCode);
        var response = Assert.IsType<ApiResponse<object>>(statusResult.Value);
        Assert.False(response.Success);
        Assert.Equal("AUTH_FAILED", response.Error?.Code);
    }

    [Fact]
    public async Task GetConversations_WithActiveSession_ReturnsConversationList()
    {
        var authService = new FakeLinkedInAuthService();
        var messagingService = new FakeLinkedInMessagingService();
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var result = await controller.GetConversations(null, null, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<ConversationListResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Single(response.Data.Conversations);
        Assert.Equal("2-conv123", response.Data.Conversations[0].Id);
    }

    [Fact]
    public async Task GetConversations_WithExpiredSession_ReturnsUnauthorized()
    {
        var authService = new FakeLinkedInAuthService();
        var messagingService = new FakeLinkedInMessagingService { ShouldFailUnauthorized = true };
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var result = await controller.GetConversations(null, null, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(401, statusResult.StatusCode);
        var response = Assert.IsType<ApiResponse<object>>(statusResult.Value);
        Assert.False(response.Success);
        Assert.Equal("UNAUTHORIZED", response.Error?.Code);
    }

    [Fact]
    public async Task GetMessages_WithValidConversationId_ReturnsMessageList()
    {
        var authService = new FakeLinkedInAuthService();
        var messagingService = new FakeLinkedInMessagingService();
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var result = await controller.GetMessages("2-conv123", null, null, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<MessageListResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal(2, response.Data.Total);
        Assert.Equal("2-conv123", response.Data.ConversationId);
    }

    [Fact]
    public async Task GetMessages_WithEmptyConversationId_ReturnsBadRequest()
    {
        var authService = new FakeLinkedInAuthService();
        var messagingService = new FakeLinkedInMessagingService();
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var result = await controller.GetMessages("", null, null, CancellationToken.None);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiResponse<object>>(badRequestResult.Value);
        Assert.False(response.Success);
        Assert.Equal("INVALID_CONVERSATION_ID", response.Error?.Code);
    }

    [Fact]
    public async Task ResendChallenge_WithValidToken_ReturnsSuccess()
    {
        var authService = new FakeLinkedInAuthService();
        var messagingService = new FakeLinkedInMessagingService();
        var controller = new LinkedInController(authService, messagingService, NullLogger<LinkedInController>.Instance);

        var request = new ResendChallengeRequest
        {
            ChallengeToken = "chk_test123"
        };

        var result = await controller.ResendChallenge(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<string>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Contains("resent", response.Data);
    }
}
