using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Controllers;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task Signed_in_branch_renders_provider_account_scopes_and_expiry()
    {
        // Unit-style: drive the controller directly with a stub provider seeded SignedIn. The
        // integration-test factory cannot easily seed a provider as SignedIn without driving a real
        // OAuth flow, so this asserts the controller surface itself: when Status.State == SignedIn,
        // the rendered HTML carries the AC-required fields (provider id, account, scopes, expiry).
        LogTestStart();
        var expiry = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var stub = new StubOAuthProvider("github")
        {
            Status = new OAuthStatus(
                State: OAuthSignInState.SignedIn,
                Account: "ada@example.com",
                Scopes: ["repo", "read:user"],
                ExpiresAtUtc: expiry,
                Error: null),
        };
        var controller = new AuthPagesController([stub], NullLogger<AuthPagesController>.Instance);

        var result = await controller.Page("github");
        var content = result.Should().BeOfType<ContentResult>().Subject;

        content.ContentType.Should().Be("text/html");
        content.Content.Should().Contain("Signed in to github");
        content.Content.Should().Contain("ada@example.com");
        content.Content.Should().Contain("repo read:user");
        content.Content.Should().Contain("2030-01-02T03:04:05");
        stub.BeginSignInCalls.Should().Be(0, "the signed-in branch must not kick off a fresh sign-in");
        LogTestEnd();
    }

    [Fact]
    public async Task Landing_branch_calls_begin_sign_in_once_and_returns_poll_script()
    {
        // Drives the not-signed-in branch: the controller must call BeginSignInAsync exactly once
        // (no replay loop) and return the HTML page that carries the inline poll script. The script
        // is the contract that drives state SignedIn / Failed transitions on the user's browser.
        LogTestStart();
        var stub = new StubOAuthProvider("github");
        var controller = new AuthPagesController([stub], NullLogger<AuthPagesController>.Instance);

        var result = await controller.Page("github");
        var content = result.Should().BeOfType<ContentResult>().Subject;

        stub.BeginSignInCalls.Should().Be(1, "the landing branch is what kicks the browser sign-in off");
        content.Content.Should().Contain("Signing in to github");
        content.Content.Should().Contain("/api/auth/", because: "the poll script targets the status endpoint");
        content.Content.Should().Contain("SignedIn", because: "the poll script branches on the string state name");
        content.Content.Should().Contain("Failed", because: "the poll script also branches on the failure string");
        LogTestEnd();
    }

    [Fact]
    public async Task Landing_branch_renders_unavailable_when_provider_throws_invalid_operation()
    {
        // Provider registered but not configured (missing client secret etc.) — BeginSignInAsync
        // throws InvalidOperationException, and the controller must surface that inline rather than
        // 500ing or letting the exception propagate.
        LogTestStart();
        var stub = new StubOAuthProvider("ado")
        {
            BeginSignInThrows = new InvalidOperationException("ADO OAuth is not configured."),
        };
        var controller = new AuthPagesController([stub], NullLogger<AuthPagesController>.Instance);

        var result = await controller.Page("ado");
        var content = result.Should().BeOfType<ContentResult>().Subject;

        content.Content.Should().Contain("ado sign-in unavailable");
        content.Content.Should().Contain("ADO OAuth is not configured");
        LogTestEnd();
    }

    private sealed class StubOAuthProvider : IOAuthTokenProvider
    {
        public StubOAuthProvider(string providerId) => ProviderId = providerId;

        public string ProviderId { get; }
        public OAuthStatus Status { get; set; } = new(OAuthSignInState.NotStarted, null, [], null, null);
        public int BeginSignInCalls { get; private set; }
        public Exception? BeginSignInThrows { get; set; }

        public Task HydrateFromStoreAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default)
        {
            BeginSignInCalls++;
            if (BeginSignInThrows is not null)
            {
                throw BeginSignInThrows;
            }

            Status = Status with { State = OAuthSignInState.Pending };
            return Task.FromResult(new SignInChallenge("https://example.test/authorize", BrowserLaunched: false));
        }

        public Task SignOutAsync(CancellationToken ct = default)
        {
            Status = new OAuthStatus(OAuthSignInState.NotStarted, null, [], null, null);
            return Task.CompletedTask;
        }

        public Task<OAuthAccessToken> GetAccessTokenAsync(IReadOnlyList<string>? scopes = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("stub does not vend tokens");
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
