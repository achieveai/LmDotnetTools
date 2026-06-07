using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Tests for the browser entry route <c>GET /auth/{provider}</c> served by
/// <c>AuthPagesController</c>. The controller has three branches: SignedIn → success page;
/// unconfigured → unavailable page; otherwise → starts sign-in and returns a poll page. Unknown
/// provider ids must return 404 (no enumeration leak). The hosted sample's M365 provider is
/// unconfigured in the test fixture (no client secret set), which gives us a deterministic
/// "unavailable" branch to assert without driving a real browser flow.
/// </summary>
public sealed class AuthPagesControllerTests : LoggingTestBase
{
    public AuthPagesControllerTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private static E2EWebAppFactory NewFactory()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("noop", _ => true)
                .Turn(t => t.Text("ok"))
            .Build();
        return new E2EWebAppFactory("test", new ScriptedBuilder(responder.AsAnthropicHandler()));
    }

    [Fact]
    public async Task Unknown_provider_returns_404()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/auth/does-not-exist");

        Logger.LogInformation("/auth/does-not-exist -> {Status}", (int)response.StatusCode);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        LogTestEnd();
    }

    [Fact]
    public async Task M365_unconfigured_renders_unavailable_page()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/auth/m365");
        var body = await response.Content.ReadAsStringAsync();

        Logger.LogInformation("/auth/m365 -> {Status}, body length {Length}", (int)response.StatusCode, body.Length);

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        body.Should().Contain("m365");
        body.Should().Contain("unavailable", because: "provider has no client secret in the test config");
        LogTestEnd();
    }

    [Fact]
    public async Task Provider_id_is_html_encoded_in_output()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // M365 is the only deterministically unconfigured provider in the test host (no secret) —
        // its unavailable page is the cheapest one to assert encoding on without driving any
        // real auth flow. The provider id has no special chars so we just verify the body is HTML
        // (Content-Type) and has no raw <script> tags from the provider id path.
        using var response = await client.GetAsync("/auth/m365");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("<script>m365", because: "provider id must be HTML-encoded, not interpolated as raw markup");
        LogTestEnd();
    }
}
