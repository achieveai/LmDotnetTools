using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.FileBrowser;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// Pins the front door that makes Bug#15's raw workspace route reachable under
/// <c>Identity:Enforce</c>: a grant in the URL PATH is the credential, and
/// <see cref="WorkspaceGrantPrincipalSource"/> turns it back into the principal it was minted for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this closes.</b> <see cref="IdentityMiddleware"/> refuses every <c>/api</c> request with no
/// resolvable principal, and it runs BEFORE routing. An <c>&lt;iframe src&gt;</c>, an <c>&lt;img src&gt;</c>
/// and a relative <c>&lt;link&gt;</c> inside a served document cannot carry an <c>Authorization</c> header.
/// So with enforcement on, the mint (an ordinary bearer-authenticated POST) succeeded, the client set the
/// iframe's <c>src</c>, every fetch 401ed at the middleware, and the user got a blank pane — with no error
/// the client could even see, because an iframe reports no status code.
/// </para>
/// <para>
/// Run through the real middleware in a real <see cref="TestServer"/> pipeline, for the same reason
/// <see cref="IdentityMiddlewareTests"/> is: the claim is about what happens to a REQUEST, and whether the
/// pipeline was entered at all is exactly what a hand-rolled context cannot show. The terminal endpoint
/// echoes the principal the middleware established, so "reached" and "reached AS the right party" are one
/// assertion rather than two.
/// </para>
/// </remarks>
public sealed class WorkspaceGrantPrincipalSourceTests
{
    private const string ThreadId = "t1";
    private const string OtherThreadId = "t2";

    private static Principal User =>
        new()
        {
            TenantId = "tnt_a",
            Actor = new PrincipalRef(PrincipalKind.EndUser, "tnt_a:oid_b"),
            Roles = new HashSet<string>(["member"], StringComparer.Ordinal),
            Source = PrincipalSource.Interactive,
        };

    private static string RawPath(string threadId, string grant, string file = "report/index.html") =>
        $"/api/conversations/{threadId}/workspace/{grant}/{file}";

    private sealed record Harness(TestServer Server, WorkspaceGrantService Grants) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Server.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A pipeline with the real identity middleware, the real grant source and a real grant service over an
    /// ephemeral key ring. Nothing stashes a bearer resolution, so the ONLY way a request can establish a
    /// principal here is the grant in its path — which is the whole point.
    /// </summary>
    private static async Task<Harness> StartAsync(bool enforce, FakeTimeProvider? time = null)
    {
        var grants = new WorkspaceGrantService(
            new EphemeralDataProtectionProvider(),
            time ?? new FakeTimeProvider(DateTimeOffset.UtcNow)
        );

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
                webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        _ = services.Configure<IdentityOptions>(o => o.Enforce = enforce);
                        _ = services.AddSingleton(TimeProvider.System);
                        _ = services.AddSingleton<ITenantStore, StubTenantStore>();
                        _ = services.AddSingleton<IAuditSink>(new RecordingAuditSink());
                        _ = services.AddSingleton(grants);
                        _ = services.AddSingleton<IRequestPrincipalSource, WorkspaceGrantPrincipalSource>();
                        _ = services.AddSingleton(sp => new PrincipalFactory(
                            sp.GetRequiredService<ITenantStore>(),
                            sp.GetRequiredService<IAuditSink>(),
                            sp.GetRequiredService<IOptions<IdentityOptions>>(),
                            TimeProvider.System,
                            sp.GetRequiredService<ILogger<PrincipalFactory>>()
                        ));
                    })
                    .Configure(app =>
                    {
                        _ = app.UseMiddleware<IdentityMiddleware>();
                        app.Run(async context =>
                        {
                            var principal = context.Items[IdentityHttpItems.PrincipalKey] as Principal;
                            await context.Response.WriteAsync(
                                $"reached:{principal?.Actor.Kind}:{principal?.Actor.Id ?? "<none>"}"
                            );
                        });
                    })
            )
            .StartAsync();

        return new Harness(host.GetTestServer(), grants);
    }

    /// <summary>
    /// The headline: with enforcement ON and NO bearer anywhere, a raw workspace GET carrying a valid grant
    /// reaches the pipeline as the principal that grant was minted for. Without the source this is a 401 and
    /// the preview pane is blank.
    /// </summary>
    [Fact]
    public async Task Enforced_RawGetWithAValidGrantAndNoBearer_IsAdmittedAsTheGrantsPrincipal()
    {
        await using var harness = await StartAsync(enforce: true);
        var grant = harness.Grants.Mint(ThreadId, User).Token;

        var response = await harness
            .Server.CreateClient()
            .GetAsync(new Uri(RawPath(ThreadId, grant), UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("reached:EndUser:tnt_a:oid_b");
    }

    /// <summary>
    /// Every subresource of a rendered page arrives the same way, so the door must admit a nested path, not
    /// only the document's own.
    /// </summary>
    [Fact]
    public async Task Enforced_NestedSubresourceUnderTheSameGrant_IsAdmittedToo()
    {
        await using var harness = await StartAsync(enforce: true);
        var grant = harness.Grants.Mint(ThreadId, User).Token;

        var response = await harness
            .Server.CreateClient()
            .GetAsync(new Uri(RawPath(ThreadId, grant, "report/img/dot.png"), UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Enforced_ForgedGrant_Is401()
    {
        await using var harness = await StartAsync(enforce: true);

        var response = await harness
            .Server.CreateClient()
            .GetAsync(new Uri(RawPath(ThreadId, "not-a-real-grant"), UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Minted from a clock two lifetimes in the past, so the expiry is genuinely behind the real clock the
    /// time-limited protector compares against.
    /// </summary>
    [Fact]
    public async Task Enforced_ExpiredGrant_Is401()
    {
        var past = DateTimeOffset.UtcNow - (FileBrowserLimits.WorkspaceGrantLifetime * 2);
        await using var harness = await StartAsync(enforce: true, new FakeTimeProvider(past));
        var grant = harness.Grants.Mint(ThreadId, User).Token;

        var response = await harness
            .Server.CreateClient()
            .GetAsync(new Uri(RawPath(ThreadId, grant), UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A grant minted for conversation A, replayed against conversation B, is <c>403</c> — not <c>401</c>.
    /// The token IS genuine, so re-minting the same one changes nothing, and answering <c>401</c> would
    /// invite the client into a refresh loop that cannot terminate.
    /// </summary>
    [Fact]
    public async Task Enforced_GrantForAnotherConversation_Is403()
    {
        await using var harness = await StartAsync(enforce: true);
        var grant = harness.Grants.Mint(ThreadId, User).Token;

        var response = await harness
            .Server.CreateClient()
            .GetAsync(new Uri(RawPath(OtherThreadId, grant), UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The door is scoped to ONE route shape. The same grant string presented on any other <c>/api</c> path
    /// authenticates nobody — otherwise a read credential for one workspace would become a general-purpose
    /// bearer for the whole API.
    /// </summary>
    [Theory]
    [InlineData("/api/conversations/t1/files")]
    [InlineData("/api/conversations/t1/files/grant")]
    [InlineData("/api/conversations/t1/messages")]
    [InlineData("/api/workspaces")]
    public async Task Enforced_TheSameGrantOnAnyOtherRoute_Is401(string path)
    {
        await using var harness = await StartAsync(enforce: true);
        var grant = harness.Grants.Mint(ThreadId, User).Token;

        var response = await harness
            .Server.CreateClient()
            .GetAsync(new Uri($"{path}?grant={Uri.EscapeDataString(grant)}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A grant minted while enforcement was off carries no principal. Presented to an enforcing host it is
    /// refused rather than admitted anonymously — the alternative is an unauthenticated read of a workspace.
    /// </summary>
    [Fact]
    public async Task Enforced_AnonymousGrant_Is401()
    {
        await using var harness = await StartAsync(enforce: true);
        var grant = harness.Grants.Mint(ThreadId, principal: null).Token;

        var response = await harness
            .Server.CreateClient()
            .GetAsync(new Uri(RawPath(ThreadId, grant), UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// With enforcement OFF nothing changes: the middleware's development principal is what the request
    /// carries, exactly as before this door existed, and a nonsense grant does not turn into a refusal.
    /// </summary>
    [Fact]
    public async Task NotEnforced_TheDoorIsInertAndTheDevelopmentPrincipalStillApplies()
    {
        await using var harness = await StartAsync(enforce: false);

        var response = await harness
            .Server.CreateClient()
            .GetAsync(new Uri(RawPath(ThreadId, "not-a-real-grant"), UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().StartWith("reached:");
    }

    // -------- The route shape the door recognises --------

    [Theory]
    [InlineData("/api/conversations/t1/workspace/g1/report/index.html")]
    [InlineData("/api/conversations/t1/workspace/g1/x")]
    // No trailing file path: still this route family, so the grant is still what authenticates it. The
    // controller answers 404 for the empty path — admitting it here leaks nothing and keeps the door's
    // shape the route's shape.
    [InlineData("/api/conversations/t1/workspace/g1")]
    public void TryReadRawWorkspaceRoute_MatchesTheRawRoute(string path)
    {
        WorkspaceGrantPrincipalSource
            .TryReadRawWorkspaceRoute(Request("GET", path), out var threadId, out var grant)
            .Should()
            .BeTrue();
        threadId.Should().Be("t1");
        grant.Should().Be("g1");
    }

    [Theory]
    [InlineData("GET", "/api/conversations/t1/files")]
    [InlineData("GET", "/api/workspaces/g1/workspace/x/y")] // "workspace" present, wrong shape
    [InlineData("GET", "/workspace/g1/x")]
    [InlineData("POST", "/api/conversations/t1/workspace/g1/report/index.html")]
    [InlineData("DELETE", "/api/conversations/t1/workspace/g1/report/index.html")]
    public void TryReadRawWorkspaceRoute_IgnoresEverythingElse(string method, string path) =>
        WorkspaceGrantPrincipalSource.TryReadRawWorkspaceRoute(Request(method, path), out _, out _).Should().BeFalse();

    private static HttpRequest Request(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context.Request;
    }
}
