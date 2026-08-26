using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// A tenant registry that answers from a dictionary. The middleware never reaches it on the paths
/// under test - it is here only to satisfy <see cref="PrincipalFactory"/>'s constructor.
/// </summary>
internal sealed class StubTenantStore : ITenantStore
{
    public Task<TenantRecord?> FindByEntraTenantIdAsync(string entraTenantId, CancellationToken ct = default) =>
        Task.FromResult<TenantRecord?>(null);

    public Task<TenantRecord?> FindByTenantIdAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<TenantRecord?>(null);

    public Task<TenantProvisionOutcome> ProvisionAsync(
        TenantRecord tenant,
        string firstAdminUpn,
        CancellationToken ct = default) =>
        Task.FromResult(TenantProvisionOutcome.Created);

    public Task<bool> TryBindFirstAdminAsync(
        string tenantId,
        string upn,
        string userId,
        DateTimeOffset boundAt,
        CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> IsTenantAdminAsync(string tenantId, string userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<EntraTenantNormalizationResult> NormalizeEntraTenantIdsAsync(CancellationToken ct = default) =>
        Task.FromResult(default(EntraTenantNormalizationResult));

    public Task<bool> TryEnsureQuarantineTenantAsync(
        string tenantId,
        DateTimeOffset createdAt,
        CancellationToken ct = default) =>
        Task.FromResult(true);
}

/// <summary>
/// Pins <see cref="IdentityMiddleware"/> against the acceptance clauses of issue #301 that are
/// about the RESPONSE rather than about the principal: what an unauthenticated request gets under
/// each setting of <c>Identity:Enforce</c>, and - the clause that exists to prevent a specific
/// user-visible bug - that a tenant refusal does not send the browser back to Entra.
/// </summary>
/// <remarks>
/// Runs the real middleware inside a real <see cref="TestServer"/> pipeline rather than calling
/// <c>InvokeAsync</c> on a <c>DefaultHttpContext</c>. The no-redirect clause is a claim about the
/// headers that reach the client, and a hand-rolled context cannot show that no other component
/// added a <c>Location</c> or <c>WWW-Authenticate</c> on the way out.
/// </remarks>
public sealed class IdentityMiddlewareTests
{
    /// <summary>Marks that the request reached the end of the pipeline rather than being refused.</summary>
    private const string ReachedBody = "reached";

    /// <summary>
    /// Builds a host running the identity middleware over a terminal endpoint that echoes
    /// <see cref="ReachedBody"/> plus the principal it was given.
    /// </summary>
    /// <param name="enforce">The <c>Identity:Enforce</c> value under test.</param>
    /// <param name="stashedResolution">
    /// A resolution to place on <see cref="HttpContext.Items"/> before the middleware runs,
    /// standing in for what the JWT bearer handler stashes after validating a token.
    /// </param>
    private static async Task<TestServer> StartAsync(
        bool enforce,
        PrincipalResolution? stashedResolution = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    _ = services.Configure<IdentityOptions>(o => o.Enforce = enforce);
                    _ = services.AddSingleton(TimeProvider.System);
                    _ = services.AddSingleton<ITenantStore, StubTenantStore>();
                    _ = services.AddSingleton<IAuditSink>(new RecordingAuditSink());
                    _ = services.AddSingleton(sp => new PrincipalFactory(
                        sp.GetRequiredService<ITenantStore>(),
                        sp.GetRequiredService<IAuditSink>(),
                        sp.GetRequiredService<IOptions<IdentityOptions>>(),
                        TimeProvider.System,
                        sp.GetRequiredService<ILogger<PrincipalFactory>>()));
                })
                .Configure(app =>
                {
                    if (stashedResolution is not null)
                    {
                        app.Use(async (context, next) =>
                        {
                            context.Items[IdentityHttpItems.ResolutionKey] = stashedResolution;
                            await next(context);
                        });
                    }

                    _ = app.UseMiddleware<IdentityMiddleware>();
                    app.Run(async context =>
                    {
                        var principal = context.Items[IdentityHttpItems.PrincipalKey] as Principal;
                        await context.Response.WriteAsync(
                            $"{ReachedBody}:{principal?.TenantId ?? "<none>"}:{principal?.Actor.Id ?? "<none>"}");
                    });
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    /// <summary>Reads the <c>code</c> field out of a refusal body.</summary>
    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task WithEnforcementOff_AnAnonymousApiRequest_RunsAsTheDevelopmentPrincipal()
    {
        using var server = await StartAsync(enforce: false);

        var response = await server.CreateClient().GetAsync(new Uri("/api/conversations", UriKind.Relative));

        // The regression gate for the whole pillar: with the flag off nothing is refused, so every
        // existing integration test keeps passing without being edited.
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = (await response.Content.ReadAsStringAsync()).Should().Be($"{ReachedBody}:legacy:dev:local");
    }

    [Fact]
    public async Task WithEnforcementOn_AnAnonymousApiRequest_IsRefusedWithUnauthorized()
    {
        using var server = await StartAsync(enforce: true);

        var response = await server.CreateClient().GetAsync(new Uri("/api/conversations", UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = (await ReadCodeAsync(response)).Should().Be("authentication_required");
    }

    [Fact]
    public async Task WithEnforcementOn_ANonApiPath_StaysReachable()
    {
        using var server = await StartAsync(enforce: true);

        // The SPA - including the screen that explains a refusal - is served from outside /api. If
        // enforcement locked it too, the user would be refused with no page able to say why.
        var response = await server.CreateClient().GetAsync(new Uri("/index.html", UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/api/identity/config")]
    [InlineData("/api/admin/tenants")]
    [InlineData("/api/health")]
    public async Task WithEnforcementOn_TheAnonymousApiPaths_StayReachable(string path)
    {
        using var server = await StartAsync(enforce: true);

        // Identity config must be readable BEFORE sign-in or the client can never start one; the
        // admin surface authenticates with the operator secret instead of a user token; health has
        // no user at all.
        var response = await server.CreateClient().GetAsync(new Uri(path, UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(PrincipalResolution.TenantNotProvisioned)]
    [InlineData(PrincipalResolution.TenantSuspended)]
    public async Task AStashedTenantRefusal_IsAnsweredWithForbiddenAndItsCode(string code)
    {
        var rejection = PrincipalResolution.Reject(code, StatusCodes.Status403Forbidden);
        using var server = await StartAsync(enforce: true, rejection);

        var response = await server.CreateClient().GetAsync(new Uri("/api/conversations", UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = (await ReadCodeAsync(response)).Should().Be(code);
    }

    [Theory]
    [InlineData("/api/lifecycle/subscriptions", PrincipalResolution.TenantNotProvisioned)]
    [InlineData("/api/lifecycle/subscriptions", PrincipalResolution.TenantSuspended)]
    [InlineData("/api/lifecycle/approvals/decisions", PrincipalResolution.TenantNotProvisioned)]
    [InlineData("/api/lifecycle/approvals/decisions", PrincipalResolution.TenantSuspended)]
    public async Task ATenantRefusal_IsAnsweredOnTheLifecycleControlPlaneToo(string path, string code)
    {
        // #402. The lifecycle plane used to be listed in InfrastructureApiPaths, so IsGuardedApiPath
        // answered false for it and this middleware returned at its first line - never reading the
        // refusal the bearer handler had already stashed. A suspended or unprovisioned tenant's
        // still-valid token therefore reached the lifecycle controllers, which authenticate off the
        // RAW ClaimsPrincipal (their own AuthenticatedAppId() reads HttpContext.User) and so saw an
        // authenticated caller. That made Identity:Enforce gate the REST front door and not this one.
        //
        // The carve-out's stated reason was that lifecycle "is gated behind its own signature check".
        // It is not: LifecycleApprovalController's own remarks say it "does not authenticate" and
        // that "no subscriber-to-host signing convention exists". So the exemption bought nothing and
        // cost tenant refusal. Lifecycle is inside the boundary now.
        var rejection = PrincipalResolution.Reject(code, StatusCodes.Status403Forbidden);
        using var server = await StartAsync(enforce: true, rejection);

        var response = await server.CreateClient().GetAsync(new Uri(path, UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = (await ReadCodeAsync(response)).Should().Be(code);
    }

    [Theory]
    [InlineData("/api/lifecycle/subscriptions")]
    [InlineData("/api/lifecycle/approvals/decisions")]
    public async Task WithEnforcementOn_AnUnauthenticatedLifecycleRequest_IsRefused(string path)
    {
        // The other half of #402: no stashed refusal at all, just a caller with no credential. Before
        // the carve-out was dropped this reached the controller; now identity answers first. Pinned
        // separately from the refusal case because the two failure modes have different codes, and a
        // fix that only answered a STASHED rejection would leave the credential-less caller in.
        using var server = await StartAsync(enforce: true);

        var response = await server.CreateClient().GetAsync(new Uri(path, UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = (await ReadCodeAsync(response)).Should().Be("authentication_required");
    }

    [Theory]
    [InlineData(PrincipalResolution.TenantNotProvisioned)]
    [InlineData(PrincipalResolution.TenantSuspended)]
    public async Task ATenantRefusal_DoesNotSendTheBrowserBackToEntra(string code)
    {
        var rejection = PrincipalResolution.Reject(code, StatusCodes.Status403Forbidden);
        using var server = await StartAsync(enforce: true, rejection);

        var response = await server.CreateClient().GetAsync(new Uri("/api/conversations", UriKind.Relative));

        // The sign-in loop this prevents: a 401 or a redirect is what makes a browser client start
        // authentication again, and starting again cannot conjure a provisioned tenant - so it
        // would retry forever. A 403 with no challenge and no redirect is a terminal answer the
        // client can render as a screen.
        _ = ((int)response.StatusCode).Should().Be(StatusCodes.Status403Forbidden);
        _ = response.Headers.Location.Should().BeNull();
        _ = response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PrincipalResolution.TenantNotProvisioned)]
    [InlineData(PrincipalResolution.TenantSuspended)]
    public async Task ATenantRefusal_RepeatsItsCodeInAHeader(string code)
    {
        var rejection = PrincipalResolution.Reject(code, StatusCodes.Status403Forbidden);
        using var server = await StartAsync(enforce: true, rejection);

        var response = await server.CreateClient().GetAsync(new Uri("/api/conversations", UriKind.Relative));

        // The SPA routes every /api call through one helper, and that helper has to classify a
        // refusal without reading the body - the body belongs to whichever caller made the request.
        _ = response.Headers.GetValues(IdentityMiddleware.RefusalCodeHeader)
            .Should().ContainSingle().Which.Should().Be(code);
    }

    [Fact]
    public async Task ATenantRefusal_IsAnsweredEvenWhileEnforcementIsOff()
    {
        var rejection = PrincipalResolution.Reject(
            PrincipalResolution.TenantNotProvisioned,
            StatusCodes.Status403Forbidden);
        using var server = await StartAsync(enforce: false, rejection);

        var response = await server.CreateClient().GetAsync(new Uri("/api/conversations", UriKind.Relative));

        // A caller who presented a token from an unprovisioned tenant gets a straight answer rather
        // than being silently downgraded to the development principal - otherwise the refusal is
        // invisible in exactly the deployment where an operator is testing the rollout.
        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = (await ReadCodeAsync(response)).Should().Be(PrincipalResolution.TenantNotProvisioned);
    }

    [Fact]
    public async Task AResolvedPrincipal_IsPublishedForTheRequestToRead()
    {
        var resolution = PrincipalResolution.Success(new Principal
        {
            TenantId = "tnt_acme",
            Actor = new PrincipalRef(PrincipalKind.EndUser, "tid-1:oid-1"),
            Source = PrincipalSource.Interactive,
        });
        using var server = await StartAsync(enforce: true, resolution);

        var response = await server.CreateClient().GetAsync(new Uri("/api/conversations", UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = (await response.Content.ReadAsStringAsync())
            .Should().Be($"{ReachedBody}:tnt_acme:tid-1:oid-1");
    }
}
