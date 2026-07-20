using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="AuthWebhookController"/>'s <see cref="IAuthWebhookForwarder"/> wiring:
/// the forwarder is invoked only on the deferred (not-immediately-signed-in) path, the target
/// resolved by <c>NotifyAuthRequiredAsync</c> is captured once and reused unchanged at whichever
/// terminal call fires, a forwarder failure never turns an allow/deny decision into a 500, and a
/// missing session id is rejected outright (it can't be resolved to a per-session secret). End-to-end
/// HTTP-pipeline coverage of the controller (secret checks, allow/deny bodies) lives in
/// <c>LmStreaming.Sample.E2E.Tests</c>; these tests isolate the forwarder seam with hand-written fakes.
/// </summary>
public sealed class AuthWebhookControllerForwarderTests
{
    private const string Secret = "test-shared-secret";

    private sealed class FakeTokenProvider(string providerId) : IOAuthTokenProvider
    {
        private OAuthAccessToken? _token;

        public string ProviderId { get; } = providerId;

        public OAuthStatus Status { get; set; } = new(OAuthSignInState.NotStarted, null, [], null, null);

        public void SetToken(OAuthAccessToken? token) => _token = token;

        public Task HydrateFromStoreAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default) =>
            Task.FromResult(new SignInChallenge("https://example.test/authorize", false));

        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<OAuthAccessToken> GetAccessTokenAsync(
            IReadOnlyList<string>? scopes = null,
            CancellationToken ct = default) =>
            _token is not null
                ? Task.FromResult(_token)
                : throw new InvalidOperationException("not signed in");
    }

    private sealed class StubResolutionPolicy(OAuthAccessToken? result) : IAuthResolutionPolicy
    {
        public Task<OAuthAccessToken?> ResolveAsync(
            IOAuthTokenProvider provider,
            IReadOnlyList<string>? scopes,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingForwarder : IAuthWebhookForwarder
    {
        public List<(string SessionId, string ProviderId, string SigninUrl, string Reason)> RequiredCalls { get; } = [];
        public List<(AuthWebhookTarget? Target, string SessionId, string ProviderId)> CompletedCalls { get; } = [];
        public List<(AuthWebhookTarget? Target, string SessionId, string ProviderId, string Reason)> DeniedCalls { get; } = [];

        public AuthWebhookTarget? TargetToReturn { get; set; }

        public Task<AuthWebhookTarget?> NotifyAuthRequiredAsync(
            string sessionId,
            string providerId,
            string signinUrl,
            string reason,
            CancellationToken ct)
        {
            RequiredCalls.Add((sessionId, providerId, signinUrl, reason));
            return Task.FromResult(TargetToReturn);
        }

        public Task NotifyAuthCompletedAsync(AuthWebhookTarget? target, string sessionId, string providerId, CancellationToken ct)
        {
            CompletedCalls.Add((target, sessionId, providerId));
            return Task.CompletedTask;
        }

        public Task NotifyAuthDeniedAsync(AuthWebhookTarget? target, string sessionId, string providerId, string reason, CancellationToken ct)
        {
            DeniedCalls.Add((target, sessionId, providerId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingForwarder : IAuthWebhookForwarder
    {
        public Task<AuthWebhookTarget?> NotifyAuthRequiredAsync(
            string sessionId,
            string providerId,
            string signinUrl,
            string reason,
            CancellationToken ct) => throw new InvalidOperationException("forwarder unreachable");

        public Task NotifyAuthCompletedAsync(AuthWebhookTarget? target, string sessionId, string providerId, CancellationToken ct) =>
            throw new InvalidOperationException("forwarder unreachable");

        public Task NotifyAuthDeniedAsync(AuthWebhookTarget? target, string sessionId, string providerId, string reason, CancellationToken ct) =>
            throw new InvalidOperationException("forwarder unreachable");
    }

    private static AuthWebhookController CreateController(
        IOAuthTokenProvider provider,
        IAuthResolutionPolicy policy,
        IAuthWebhookForwarder forwarder)
    {
        var authOptions = new AuthOptions();
        var sessionSecretStore = new SessionSecretStore(
            Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
            NullLogger<SessionSecretStore>.Instance);
        // Tests below use "session-1" and "session-2" as their request session ids — seed both with
        // the same known Secret so a fixed Authorization header can authenticate either one.
        sessionSecretStore.SaveAsync("session-1", Secret).GetAwaiter().GetResult();
        sessionSecretStore.SaveAsync("session-2", Secret).GetAwaiter().GetResult();

        var controller = new AuthWebhookController(
            [provider],
            sessionSecretStore,
            policy,
            forwarder,
            authOptions,
            NullLogger<AuthWebhookController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = Secret;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static AuthWebhookRequest NewRequest(string? sessionId, string providerId = "github") => new()
    {
        SessionId = sessionId,
        AppId = "lmstreaming-sample",
        ProviderId = providerId,
        RuleId = providerId,
        DestinationHost = "api.github.com",
        DestinationPort = 443,
        Method = "GET",
        Path = "/user",
        RequiredScopes = [],
    };

    private static OAuthAccessToken NewToken() => new("tok-abc", DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task Immediately_signed_in_allow_never_touches_the_forwarder()
    {
        var provider = new FakeTokenProvider("github");
        provider.SetToken(NewToken());
        var forwarder = new RecordingForwarder();
        var controller = CreateController(provider, new StubResolutionPolicy(null), forwarder);

        var result = await controller.Evaluate("github", NewRequest("session-1"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        forwarder.RequiredCalls.Should().BeEmpty("an immediately-available token never reaches the deferred/forwarder path");
        forwarder.CompletedCalls.Should().BeEmpty();
        forwarder.DeniedCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Deferred_deny_forwards_required_then_denied_with_the_same_captured_target()
    {
        var provider = new FakeTokenProvider("github"); // no token: falls through to the policy
        var forwarder = new RecordingForwarder { TargetToReturn = new AuthWebhookTarget("thread-1", "run-1", "https://caller.test/hook") };
        var controller = CreateController(provider, new StubResolutionPolicy(null), forwarder);

        var result = await controller.Evaluate("github", NewRequest("session-1"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        forwarder.RequiredCalls.Should().ContainSingle().Which.SessionId.Should().Be("session-1");
        forwarder.CompletedCalls.Should().BeEmpty();
        forwarder.DeniedCalls.Should().ContainSingle()
            .Which.Target.Should().Be(forwarder.TargetToReturn, "the terminal call must reuse the target captured at auth_required, not re-resolve it");
    }

    [Fact]
    public async Task Forwarded_signin_url_is_absolute_not_the_same_origin_relative_path()
    {
        var provider = new FakeTokenProvider("github"); // no token: falls through to the policy
        var forwarder = new RecordingForwarder();
        var controller = CreateController(provider, new StubResolutionPolicy(null), forwarder);

        await controller.Evaluate("github", NewRequest("session-1"), CancellationToken.None);

        forwarder.RequiredCalls.Should().ContainSingle()
            .Which.SigninUrl.Should().Be(
                "http://127.0.0.1:5000/auth/github",
                "an external webhook receiver cannot resolve a same-origin relative path");
    }

    [Fact]
    public async Task Deferred_allow_forwards_required_then_completed_with_the_same_captured_target()
    {
        var provider = new FakeTokenProvider("github"); // no token: falls through to the policy
        var forwarder = new RecordingForwarder { TargetToReturn = new AuthWebhookTarget("thread-2", null, "https://caller.test/hook") };
        var controller = CreateController(provider, new StubResolutionPolicy(NewToken()), forwarder);

        var result = await controller.Evaluate("github", NewRequest("session-2"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        forwarder.RequiredCalls.Should().ContainSingle("the target is resolved exactly once per webhook call, never re-resolved for the terminal outcome");
        forwarder.CompletedCalls.Should().ContainSingle()
            .Which.Target.Should().Be(forwarder.TargetToReturn);
        forwarder.DeniedCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_session_id_is_rejected_before_ever_reaching_the_forwarder()
    {
        // Per-session secrets need the session id up front to know which secret to check against —
        // a missing session id can no longer fall through to an Ok allow/deny decision, unlike the
        // old global-secret design where the id was only needed for the (optional) forwarder calls.
        var provider = new FakeTokenProvider("github");
        var forwarder = new RecordingForwarder { TargetToReturn = new AuthWebhookTarget("thread-3", null, "https://caller.test/hook") };
        var controller = CreateController(provider, new StubResolutionPolicy(null), forwarder);

        var result = await controller.Evaluate("github", NewRequest(sessionId: null), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        forwarder.RequiredCalls.Should().BeEmpty();
        forwarder.CompletedCalls.Should().BeEmpty();
        forwarder.DeniedCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Secret_valid_for_another_session_is_rejected()
    {
        // The cross-session-isolation guarantee: session A's real secret must not authenticate a
        // call claiming to be session B, even though both sessions are known to the store.
        var sessionSecretStore = new SessionSecretStore(
            Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
            NullLogger<SessionSecretStore>.Instance);
        await sessionSecretStore.SaveAsync("session-a", "secret-for-a");
        await sessionSecretStore.SaveAsync("session-b", "secret-for-b");

        var provider = new FakeTokenProvider("github");
        var forwarder = new RecordingForwarder();
        var controller = new AuthWebhookController(
            [provider],
            sessionSecretStore,
            new StubResolutionPolicy(null),
            forwarder,
            new AuthOptions(),
            NullLogger<AuthWebhookController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "secret-for-a";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.Evaluate("github", NewRequest("session-b"), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        forwarder.RequiredCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Forwarder_failure_never_breaks_the_always_200_contract()
    {
        var provider = new FakeTokenProvider("github");
        var controller = CreateController(provider, new StubResolutionPolicy(NewToken()), new ThrowingForwarder());

        var result = await controller.Evaluate("github", NewRequest("session-1"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("a forwarder exception must be logged and swallowed, never surfaced as a 500");
        var ok = (OkObjectResult)result;
        ((AuthWebhookResponse)ok.Value!).Decision.Should().Be("allow");
    }

    [Fact]
    public async Task Default_no_op_forwarder_preserves_pre_area3_deny_behavior()
    {
        var provider = new FakeTokenProvider("github");
        var controller = CreateController(provider, new StubResolutionPolicy(null), new NoOpAuthWebhookForwarder());

        var result = await controller.Evaluate("github", NewRequest("session-1"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ((AuthWebhookResponse)ok.Value!).Decision.Should().Be("deny");
    }

    [Fact]
    public async Task Default_no_op_forwarder_preserves_pre_area3_allow_behavior()
    {
        var provider = new FakeTokenProvider("github");
        var controller = CreateController(provider, new StubResolutionPolicy(NewToken()), new NoOpAuthWebhookForwarder());

        var result = await controller.Evaluate("github", NewRequest("session-1"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ((AuthWebhookResponse)ok.Value!).Decision.Should().Be("allow");
    }
}
