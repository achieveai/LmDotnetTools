using System.Net;
using System.Security.Claims;
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
    /// <param name="preAuthenticated">
    /// A principal to place on <see cref="HttpContext.User"/> before the middleware runs, standing
    /// in for what <c>UseAuthentication</c> leaves behind when a scheme validated the request.
    /// <c>UseSampleIdentity</c> runs that middleware ahead of this one, so this is not a synthetic
    /// arrangement - it is the ordering the sample ships.
    /// </param>
    private static async Task<TestServer> StartAsync(
        bool enforce,
        PrincipalResolution? stashedResolution = null,
        ClaimsPrincipal? preAuthenticated = null)
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
                    if (stashedResolution is not null || preAuthenticated is not null)
                    {
                        app.Use(async (context, next) =>
                        {
                            if (stashedResolution is not null)
                            {
                                context.Items[IdentityHttpItems.ResolutionKey] = stashedResolution;
                            }

                            if (preAuthenticated is not null)
                            {
                                context.User = preAuthenticated;
                            }

                            await next(context);
                        });
                    }

                    _ = app.UseMiddleware<IdentityMiddleware>();
                    app.Run(async context =>
                    {
                        var principal = context.Items[IdentityHttpItems.PrincipalKey] as Principal;
                        await context.Response.WriteAsync(
                            $"{ReachedBody}:{principal?.TenantId ?? "<none>"}:{principal?.Actor.Id ?? "<none>"}"
                                + $"|{DescribeUser(context.User)}");
                    });
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    /// <summary>
    /// The claims principal the request carries, as authentication type and name identifier - the
    /// two facts the lifecycle control plane's <c>AuthenticatedAppId()</c> reads (#424).
    /// </summary>
    private static string DescribeUser(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true
            ? $"{user.Identity.AuthenticationType}:{user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "<no-nameid>"}"
            : "<anonymous>";

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
        // The trailing segment is the #424 bridge's regression gate on this same path: the
        // development principal names no app, so nothing is projected onto User and an anonymous
        // request stays anonymous to everything that reads a claims principal.
        _ = (await response.Content.ReadAsStringAsync())
            .Should().Be($"{ReachedBody}:legacy:dev:local|<anonymous>");
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
    public async Task AWebSocketHandshakeWithNoCredential_IsRefusedWithForbidden_NotUnauthorized()
    {
        using var server = await StartAsync(enforce: true);

        var response = await server.CreateClient().GetAsync(new Uri("/ws?threadId=t", UriKind.Relative));

        // 401 is the one status a browser answers by re-authenticating, and re-authenticating cannot
        // attach a credential to a handshake that carried none - so a 401 here loops (#342/#341).
        _ = ((int)response.StatusCode).Should().Be(StatusCodes.Status403Forbidden);
        _ = response.Headers.WwwAuthenticate.Should().BeEmpty();
        _ = (await ReadCodeAsync(response)).Should().Be(IdentityMiddleware.WebSocketRefusalCode);
    }

    [Fact]
    public async Task TheRestSurfaceKeepsIts401_WhileTheWebSocketTransportDoesNot()
    {
        using var server = await StartAsync(enforce: true);
        using var client = server.CreateClient();

        var rest = await client.GetAsync(new Uri("/api/conversations", UriKind.Relative));
        var socket = await client.GetAsync(new Uri("/ws?threadId=t", UriKind.Relative));

        // The pair, in one test, because the value of each answer is that it DIFFERS from the other.
        // On REST, re-authenticating is exactly the fix, so 401 is right there and wrong on /ws.
        _ = ((int)rest.StatusCode).Should().Be(StatusCodes.Status401Unauthorized);
        _ = ((int)socket.StatusCode).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void ASubprotocolCredential_IsPromotedIntoAuthorizationAndRemovedFromTheOfferedList()
    {
        var request = WebSocketRequest(
            "/ws",
            $"{IdentityMiddleware.WebSocketCredentialSubProtocolPrefix}token-abc, "
                + IdentityMiddleware.WebSocketSubProtocol);

        _ = IdentityMiddleware.PromoteWebSocketCredential(request).Should().BeTrue();

        _ = request.Headers.Authorization.ToString().Should().Be("Bearer token-abc");

        // Removed, not merely ignored: a token left in a request header travels into every request
        // log and diagnostic dump downstream, and the accept must never echo it back either.
        _ = request.Headers["Sec-WebSocket-Protocol"].ToString()
            .Should().Be(IdentityMiddleware.WebSocketSubProtocol);
        _ = IdentityMiddleware.NegotiateWebSocketSubProtocol(request)
            .Should().Be(IdentityMiddleware.WebSocketSubProtocol);
    }

    /// <summary>
    /// Precedence and stripping are two separate promises, and the header check used to answer both
    /// with one early return: a request that already carried <c>Authorization</c> kept its
    /// <c>Authorization</c> - correct - and ALSO kept the offered <c>lm.bearer.&lt;token&gt;</c> in
    /// <c>Sec-WebSocket-Protocol</c> for the rest of the pipeline, which the method's own remark says
    /// never happens. Stripping is unconditional; only the promotion defers.
    /// </summary>
    [Fact]
    public void AnAuthorizationHeaderAlreadyOnTheRequest_IsNeverOverwrittenByASubprotocol_AndTheTokenIsStillStripped()
    {
        var request = WebSocketRequest(
            "/ws",
            $"{IdentityMiddleware.WebSocketCredentialSubProtocolPrefix}attacker-token, "
                + IdentityMiddleware.WebSocketSubProtocol);
        request.Headers.Authorization = "Bearer real-token";

        _ = IdentityMiddleware.PromoteWebSocketCredential(request).Should().BeFalse();
        _ = request.Headers.Authorization.ToString().Should().Be("Bearer real-token");

        // The credential is gone from the offered list even though it was not promoted. Whether a
        // token is honoured and whether it travels onward into logs, diagnostics and the accept's
        // echo are unrelated questions, and the second answer must not depend on the first.
        _ = request.Headers["Sec-WebSocket-Protocol"].ToString()
            .Should().Be(IdentityMiddleware.WebSocketSubProtocol);
        _ = request.Headers["Sec-WebSocket-Protocol"].ToString()
            .Should().NotContain("attacker-token");
    }

    /// <summary>
    /// A handshake may offer more than one <c>lm.bearer.*</c> entry, and every one of them must leave
    /// the request. The strip decision used to be fused to the promotion decision - the first match
    /// became the credential and every LATER match fell through to the keep list - so a client that
    /// offered two tokens got the second one written straight back into
    /// <c>Sec-WebSocket-Protocol</c>, which is the exact leak the strip exists to prevent.
    /// </summary>
    /// <remarks>
    /// The promotion half is unchanged and asserted alongside: at most one credential is honoured, and
    /// it is the first offered. Only the stripping is unconditional.
    /// </remarks>
    [Fact]
    public void EveryCredentialSubprotocolIsStripped_NotJustTheOneThatGetsPromoted()
    {
        var request = WebSocketRequest(
            "/ws",
            $"{IdentityMiddleware.WebSocketCredentialSubProtocolPrefix}first-token, "
                + $"{IdentityMiddleware.WebSocketCredentialSubProtocolPrefix}second-token, "
                + IdentityMiddleware.WebSocketSubProtocol);

        _ = IdentityMiddleware.PromoteWebSocketCredential(request).Should().BeTrue();
        _ = request.Headers.Authorization.ToString().Should().Be("Bearer first-token");

        var offered = request.Headers["Sec-WebSocket-Protocol"].ToString();
        _ = offered.Should().Be(IdentityMiddleware.WebSocketSubProtocol);
        _ = offered.Should().NotContain("second-token");
        _ = offered.Should().NotContain(IdentityMiddleware.WebSocketCredentialSubProtocolPrefix);
    }

    /// <summary>
    /// A bare <c>lm.bearer.</c> with no token behind it leaves the request too. It carries nothing to
    /// promote, which is exactly why it used to survive: the method returned early on "no credential
    /// found" BEFORE writing the filtered list back, so the original header - prefix and all - stayed
    /// on the request untouched.
    /// </summary>
    /// <remarks>
    /// Nothing secret leaks in this case; the point is that the stripping promise is unconditional, and
    /// a promise with a silent exception in it is not one a reader can rely on. It is also the cheapest
    /// possible check that the write-back happens on the no-credential path at all.
    /// </remarks>
    [Fact]
    public void ABareCredentialPrefixWithNoTokenIsStripped_EvenThoughNothingIsPromoted()
    {
        var request = WebSocketRequest(
            "/ws",
            $"{IdentityMiddleware.WebSocketCredentialSubProtocolPrefix}, "
                + IdentityMiddleware.WebSocketSubProtocol);

        // Nothing to promote, so the return value is false - and the header is still cleaned.
        _ = IdentityMiddleware.PromoteWebSocketCredential(request).Should().BeFalse();
        _ = request.Headers.Authorization.ToString().Should().BeEmpty();

        var offered = request.Headers["Sec-WebSocket-Protocol"].ToString();
        _ = offered.Should().Be(IdentityMiddleware.WebSocketSubProtocol);
        _ = offered.Should().NotContain(IdentityMiddleware.WebSocketCredentialSubProtocolPrefix);
    }

    [Fact]
    public void ASubprotocolCredential_IsIgnoredOutsideTheWebSocketTransports()
    {
        var request = WebSocketRequest(
            "/api/conversations",
            $"{IdentityMiddleware.WebSocketCredentialSubProtocolPrefix}token-abc");

        // The promotion exists because a browser cannot set a header on a handshake. Everywhere else
        // it can, so honouring the subprotocol would add a second, weaker way to present a credential
        // to routes that already have a strong one.
        _ = IdentityMiddleware.PromoteWebSocketCredential(request).Should().BeFalse();
        _ = request.Headers.Authorization.ToString().Should().BeEmpty();
    }

    [Fact]
    public void AHandshakeOfferingOnlyApplicationSubprotocols_PromotesNothingAndKeepsThemAll()
    {
        var request = WebSocketRequest("/ws", IdentityMiddleware.WebSocketSubProtocol);

        _ = IdentityMiddleware.PromoteWebSocketCredential(request).Should().BeFalse();
        _ = request.Headers.Authorization.ToString().Should().BeEmpty();
        _ = request.Headers["Sec-WebSocket-Protocol"].ToString()
            .Should().Be(IdentityMiddleware.WebSocketSubProtocol);
    }

    [Fact]
    public void AHandshakeOfferingOnlyTheCredential_LeavesNothingForTheAcceptToSelect()
    {
        var request = WebSocketRequest(
            "/ws",
            $"{IdentityMiddleware.WebSocketCredentialSubProtocolPrefix}token-abc");

        _ = IdentityMiddleware.PromoteWebSocketCredential(request).Should().BeTrue();

        // RFC 6455 lets the server select at most one subprotocol the client offered. With the
        // credential consumed there is no candidate left, and the accept must select none rather
        // than echo the credential.
        _ = IdentityMiddleware.NegotiateWebSocketSubProtocol(request).Should().BeNull();
        _ = request.Headers.ContainsKey("Sec-WebSocket-Protocol").Should().BeFalse();
    }

    [Theory]
    [InlineData("/ws", true)]
    [InlineData("/ws/subagent", true)]
    [InlineData("/wsx", false)]
    [InlineData("/api/conversations", false)]
    public void TheWebSocketPredicate_MatchesBySegment(string path, bool expected) =>
        IdentityMiddleware.IsGuardedWebSocketPath(new PathString(path)).Should().Be(expected);

    /// <summary>Builds a request on <paramref name="path"/> offering <paramref name="offered"/>.</summary>
    private static HttpRequest WebSocketRequest(string path, string offered)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = new PathString(path);
        context.Request.Headers["Sec-WebSocket-Protocol"] = offered;
        return context.Request;
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

        // An end-user principal names no app either, so it too is published on Items alone.
        _ = (await response.Content.ReadAsStringAsync())
            .Should().Be($"{ReachedBody}:tnt_acme:tid-1:oid-1|<anonymous>");
    }

    [Fact]
    public async Task AnAppBearingPrincipal_IsAlsoPublishedAsTheRequestsClaimsPrincipal()
    {
        // #424. Publishing on Items alone reaches only code that can see this sample's Principal
        // type. The lifecycle control plane cannot - it lives in LmAgentInfra - and authenticates
        // off HttpContext.User, so a service caller identity had just accepted was refused by it as
        // unauthenticated. The name identifier is asserted by VALUE because that is what the plane
        // resolves an owner from: a projection that carried the tenant, or a constant, would still
        // be "authenticated" and would silently file every app's subscriptions under one owner.
        using var server = await StartAsync(enforce: true, PrincipalResolution.Success(new Principal
        {
            TenantId = "tnt_daemon",
            Actor = new PrincipalRef(PrincipalKind.App, "review-daemon"),
            AppId = "review-daemon",
            Source = PrincipalSource.AppOnly,
        }));

        var response = await server.CreateClient().GetAsync(new Uri("/api/conversations", UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = (await response.Content.ReadAsStringAsync())
            .Should().EndWith($"|{PrincipalFactory.BridgedAuthenticationType}:review-daemon");
    }

    [Fact]
    public async Task AnAlreadyAuthenticatedRequest_KeepsTheClaimsItsOwnSchemeEstablished()
    {
        // UseSampleIdentity runs UseAuthentication ahead of this middleware, so with an Entra app
        // registration configured the bearer handler has already populated User with the token's
        // own claims by the time the bridge runs. Overwriting those would narrow a real identity to
        // the three claims the projection knows about - and would do it silently, since the
        // replacement is also a well-formed authenticated principal. The bridge is a fill-in, and a
        // fill-in that overwrites is not one.
        //
        // The stashed resolution below is AppOnly, not Interactive - a shape the real interactive
        // front door never stashes (PrincipalFactory.ResolveInteractiveAsync only ever produces
        // Source=Interactive, AppId=null). It is used here to drive BridgeToHttpUser through its
        // would-bridge branch directly, so this test can pin the overwrite guard in isolation. Do
        // not read it as evidence that an app-shaped principal reaches the interactive stash in
        // production - it does not.
        var bearerIdentity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "entra-object-id"),
                new Claim("scp", "Conversations.ReadWrite"),
            ],
            "Bearer");

        using var server = await StartAsync(
            enforce: true,
            PrincipalResolution.Success(new Principal
            {
                TenantId = "tnt_daemon",
                Actor = new PrincipalRef(PrincipalKind.App, "review-daemon"),
                AppId = "review-daemon",
                Source = PrincipalSource.AppOnly,
            }),
            new ClaimsPrincipal(bearerIdentity));

        var response = await server.CreateClient().GetAsync(new Uri("/api/conversations", UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = (await response.Content.ReadAsStringAsync())
            .Should().EndWith("|Bearer:entra-object-id");
    }
}
