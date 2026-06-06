using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Auth;
using LmStreaming.Sample.Services.Discovery;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Tests for <see cref="ContextDiscoveryController.NotifyAsync"/>: 401 on missing/wrong shared
/// secret, 400 on malformed payload, 200 on the happy path (including best-effort no-ops when
/// the session/binding aren't known yet — discovery is an enrichment, not a precondition).
/// </summary>
public class ContextDiscoveryControllerTests
{
    private const string Secret = "test-shared-secret-1234";
    private const string GatewayBaseUrl = "http://localhost:3000";

    private static ContextDiscoveryController CreateController(
        string? authorizationHeader,
        SandboxSessionRegistry? registry = null,
        WorkspaceSubAgentLoader? loader = null)
    {
        var sharedSecret = new AuthSharedSecret(new AuthOptions
        {
            Webhook = new WebhookOptions { GatewaySharedSecret = Secret },
        });

        registry ??= CreateEmptyRegistry();
        loader ??= new WorkspaceSubAgentLoader(registry, NullLogger<WorkspaceSubAgentLoader>.Instance);

        var controller = new ContextDiscoveryController(
            sharedSecret,
            registry,
            loader,
            NullLogger<ContextDiscoveryController>.Instance);

        var httpContext = new DefaultHttpContext();
        if (authorizationHeader is not null)
        {
            httpContext.Request.Headers.Authorization = authorizationHeader;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static SandboxSessionRegistry CreateEmptyRegistry()
    {
        // No HTTP traffic occurs in these tests — the controller's session/binding lookups go
        // through the in-memory dictionaries only — so a stub handler that throws on use is the
        // safest contract.
        static HttpResponseMessage UnusedRespond(HttpRequestMessage _) =>
            throw new InvalidOperationException("HTTP not expected in ContextDiscoveryControllerTests");

        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(UnusedRespond)));

        return new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(UnusedRespond)),
            new AuthOptions(),
            new AuthSharedSecret(new AuthOptions()));
    }

    [Fact]
    public async Task NotifyAsync_NoAuthorizationHeader_ReturnsUnauthorized()
    {
        var controller = CreateController(authorizationHeader: null);
        var body = new ContextDiscoveryPayload { Kind = "subagent", Name = "echo" };

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task NotifyAsync_WrongSecret_ReturnsUnauthorized()
    {
        var controller = CreateController(authorizationHeader: "wrong-secret");
        var body = new ContextDiscoveryPayload { Kind = "subagent", Name = "echo" };

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_NullBody_ReturnsBadRequest()
    {
        var controller = CreateController(authorizationHeader: Secret);

        var result = await controller.NotifyAsync(null, CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_MissingKind_ReturnsBadRequest()
    {
        var controller = CreateController(authorizationHeader: Secret);
        var body = new ContextDiscoveryPayload { Name = "echo" };

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_MissingName_ReturnsBadRequest()
    {
        var controller = CreateController(authorizationHeader: Secret);
        var body = new ContextDiscoveryPayload { Kind = "subagent" };

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_WellFormedPayload_ReturnsOk()
    {
        var controller = CreateController(authorizationHeader: Secret);
        var body = new ContextDiscoveryPayload
        {
            Kind = "subagent",
            Name = "echo",
            Description = "Echoes a marker.",
            Path = ".claude/agents/echo.md",
        };

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task NotifyAsync_NonSubAgentKind_NoSession_ReturnsOk()
    {
        // Non-subagent kinds are still logged as discoveries but never activate — the controller
        // must NOT try to resolve a session for them. Verifies that path stays a no-op even when
        // session_id is absent from the payload.
        var controller = CreateController(authorizationHeader: Secret);
        var body = new ContextDiscoveryPayload
        {
            Kind = "skill",
            Name = "review-skill",
            Path = ".claude/skills/review.md",
        };

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task NotifyAsync_SubAgentPayload_UnknownSession_ReturnsOkWithoutActivation()
    {
        // Discovery firing before the agent path has initialised the session must NOT fail —
        // the gateway shouldn't see a 5xx for an enrichment event. Best-effort no-op + 200 is
        // the contract.
        var registry = CreateEmptyRegistry();
        var controller = CreateController(
            authorizationHeader: Secret,
            registry: registry);

        var body = new ContextDiscoveryPayload
        {
            SessionId = "session-not-in-registry",
            Kind = "subagent",
            Name = "ghost",
            Path = ".claude/agents/ghost.md",
        };

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_respond(request));
        }
    }
}
