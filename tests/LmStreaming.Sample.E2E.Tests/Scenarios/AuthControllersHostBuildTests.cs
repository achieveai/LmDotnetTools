using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Controllers;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Services.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Resolve-time guard: the per-provider controllers (<see cref="AdoAuthController"/>,
/// <see cref="GitHubAuthController"/>) take their <em>concrete</em> provider in the ctor, while the
/// webhook + hydrator still take <see cref="IEnumerable{IOAuthTokenProvider}"/>. The host's
/// dual-registration (concrete + <see cref="IOAuthTokenProvider"/> alias-to-concrete) must satisfy
/// both shapes from a single singleton instance per provider — if a future contributor forgets to
/// dual-register a new provider, the per-provider controller fails to resolve at boot. This test
/// catches that early, with no agent + no network.
/// </summary>
public sealed class AuthControllersHostBuildTests : LoggingTestBase
{
    public AuthControllersHostBuildTests(ITestOutputHelper output)
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
    public void Host_resolves_concrete_providers_from_DI()
    {
        LogTestStart();
        using var factory = NewFactory();
        // Accessing Services forces host build.
        var services = factory.Services;

        Logger.LogInformation("Resolving concrete OAuth providers (used by per-provider controllers).");
        var github = services.GetRequiredService<GitHubOAuthProvider>();
        var ado = services.GetRequiredService<AdoOAuthProvider>();
        var m365 = services.GetRequiredService<M365OAuthProvider>();

        github.Should().NotBeNull();
        ado.Should().NotBeNull();
        m365.Should().NotBeNull();
        LogTestEnd();
    }

    [Fact]
    public void Concrete_provider_and_IOAuthTokenProvider_alias_resolve_to_the_same_instance()
    {
        LogTestStart();
        using var factory = NewFactory();
        var services = factory.Services;

        // The alias registration (sp => sp.GetRequiredService<TConcrete>()) means there's a single
        // singleton instance per provider — the webhook's IEnumerable<IOAuthTokenProvider> and the
        // per-provider controllers see the SAME object, so token state can't drift between them.
        var githubConcrete = services.GetRequiredService<GitHubOAuthProvider>();
        var adoConcrete = services.GetRequiredService<AdoOAuthProvider>();
        var m365Concrete = services.GetRequiredService<M365OAuthProvider>();
        var allProviders = services.GetRequiredService<IEnumerable<IOAuthTokenProvider>>().ToList();

        Logger.LogInformation(
            "Enumerable IOAuthTokenProvider resolved {Count} providers; verifying singleton-aliased identity.",
            allProviders.Count);

        allProviders.Should().Contain(p => ReferenceEquals(p, githubConcrete));
        allProviders.Should().Contain(p => ReferenceEquals(p, adoConcrete));
        allProviders.Should().Contain(p => ReferenceEquals(p, m365Concrete));
        LogTestEnd();
    }

    [Fact]
    public async Task Per_provider_controllers_route_under_their_own_base()
    {
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Status is a tolerant GET (always returns a status record even when not signed in), so it
        // doubles as a smoke test that each controller's [Route("api/auth/{provider}")] is wired.
        using var githubResp = await client.GetAsync("/api/auth/github/status");
        using var adoResp = await client.GetAsync("/api/auth/ado/status");
        using var m365Resp = await client.GetAsync("/api/auth/m365/status");

        Logger.LogInformation(
            "github/status -> {GhStatus}, ado/status -> {AdoStatus}, m365/status -> {M365Status}",
            (int)githubResp.StatusCode,
            (int)adoResp.StatusCode,
            (int)m365Resp.StatusCode);

        githubResp.IsSuccessStatusCode.Should().BeTrue();
        adoResp.IsSuccessStatusCode.Should().BeTrue();
        m365Resp.IsSuccessStatusCode.Should().BeTrue();
        LogTestEnd();
    }

    [Theory]
    [InlineData("/api/auth/github/status")]
    [InlineData("/api/auth/ado/status")]
    [InlineData("/api/auth/m365/status")]
    public async Task Status_serializes_state_as_string_name_for_landing_page_poll(string statusPath)
    {
        // Landing-page poll in AuthPagesController compares s.state === 'SignedIn' / 'Failed'.
        // If OAuthSignInState ever serializes as the underlying integer (MVC's STJ default), every
        // comparison goes dead and the page polls forever. The attribute-scoped [JsonStringEnumConverter]
        // on the enum is what pins this contract.
        LogTestStart();
        using var factory = NewFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var resp = await client.GetAsync(statusPath);
        var json = await resp.Content.ReadAsStringAsync();
        Logger.LogInformation("{Path} -> {Body}", statusPath, json);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var stateProp = doc.RootElement.GetProperty("state");
        stateProp.ValueKind.Should().Be(System.Text.Json.JsonValueKind.String);
        // Tolerant set — providers without config land in NotStarted; tests only need to assert
        // that whatever value comes back is one of the documented string names.
        stateProp.GetString().Should().BeOneOf("NotStarted", "Pending", "SignedIn", "Failed");
        LogTestEnd();
    }
}
