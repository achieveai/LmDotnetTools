using LmStreaming.Sample.Services.Auth;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Tests for <see cref="ContextDiscoveryController.Notify"/>: 401 on missing/wrong shared
/// secret, 400 on malformed payload, 200 on the happy path. The handler is intentionally
/// thin (log-only), so the contract under test is the auth + payload validation it pins.
/// </summary>
public class ContextDiscoveryControllerTests
{
    private const string Secret = "test-shared-secret-1234";

    private static ContextDiscoveryController CreateController(string? authorizationHeader)
    {
        var sharedSecret = new AuthSharedSecret(new AuthOptions
        {
            Webhook = new WebhookOptions { GatewaySharedSecret = Secret },
        });
        var controller = new ContextDiscoveryController(
            sharedSecret,
            NullLogger<ContextDiscoveryController>.Instance);

        var httpContext = new DefaultHttpContext();
        if (authorizationHeader is not null)
        {
            httpContext.Request.Headers.Authorization = authorizationHeader;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    [Fact]
    public void Notify_NoAuthorizationHeader_ReturnsUnauthorized()
    {
        var controller = CreateController(authorizationHeader: null);
        var body = new ContextDiscoveryPayload { Kind = "subagent", Name = "echo" };

        var result = controller.Notify(body);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public void Notify_WrongSecret_ReturnsUnauthorized()
    {
        var controller = CreateController(authorizationHeader: "wrong-secret");
        var body = new ContextDiscoveryPayload { Kind = "subagent", Name = "echo" };

        var result = controller.Notify(body);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public void Notify_CorrectSecret_NullBody_ReturnsBadRequest()
    {
        var controller = CreateController(authorizationHeader: Secret);

        var result = controller.Notify(null);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public void Notify_CorrectSecret_MissingKind_ReturnsBadRequest()
    {
        var controller = CreateController(authorizationHeader: Secret);
        var body = new ContextDiscoveryPayload { Name = "echo" };

        var result = controller.Notify(body);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public void Notify_CorrectSecret_MissingName_ReturnsBadRequest()
    {
        var controller = CreateController(authorizationHeader: Secret);
        var body = new ContextDiscoveryPayload { Kind = "subagent" };

        var result = controller.Notify(body);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public void Notify_CorrectSecret_WellFormedPayload_ReturnsOk()
    {
        var controller = CreateController(authorizationHeader: Secret);
        var body = new ContextDiscoveryPayload
        {
            Kind = "subagent",
            Name = "echo",
            Description = "Echoes a marker.",
            Path = ".claude/agents/echo.md",
        };

        var result = controller.Notify(body);

        result.Should().BeOfType<OkResult>();
    }
}
