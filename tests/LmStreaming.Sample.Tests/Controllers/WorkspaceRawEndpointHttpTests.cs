using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.FileBrowser;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// The raw workspace route over REAL HTTP, through the real host pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WorkspaceRawEndpointTests"/> invokes the action directly, which is the right shape for the
/// policy questions (which header, which status, which refusal) but cannot see anything the PIPELINE does.
/// Three things live only out here, and each one was asserted nowhere before this file: the routing layer's
/// own decoding of the catch-all segment (a <c>%2e%2e</c> is <c>..</c> by the time the action sees it, so
/// the refusal has to survive decoding rather than pattern-match the escaped form); the ordering of
/// <c>[ApiController]</c> model validation against the <c>InboundS2SAuth</c> filter; and whether the headers
/// set on <c>Response</c> survive <see cref="Microsoft.AspNetCore.Mvc.FileContentResult"/> actually
/// executing and writing a body.
/// </para>
/// <para>
/// Only the two edges that cannot run in a test process are substituted — the conversation store and the
/// sandbox file browser. The controller, the routing, the content-type map, the grant service and every
/// header are the production types. <c>Identity:Enforce</c> is off here, which is the default deployment;
/// the enforcing case is <see cref="Identity.WorkspaceGrantPrincipalSourceTests"/>, where the claim is about
/// the identity middleware rather than about the route.
/// </para>
/// </remarks>
public sealed class WorkspaceRawEndpointHttpTests
{
    private const string ThreadId = "t1";

    private static readonly byte[] IndexHtml = Encoding.UTF8.GetBytes("<!doctype html><p>hi</p>");

    private sealed class RawEndpointHost : WebApplicationFactory<Program>
    {
        public FakeFileBrowser Browser { get; } = new();

        public RawEndpointHost()
        {
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
            Browser.Listings[""] = [new SandboxDirectoryEntry("report", SandboxEntryType.Directory, null, false)];
            Browser.Listings["report"] =
            [
                new SandboxDirectoryEntry("index.html", SandboxEntryType.File, IndexHtml.Length, false),
            ];
            Browser.FileBytes = IndexHtml;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Production: no Vite dev-server auto-spawn. Matches the other in-process host tests here.
            builder.UseEnvironment("Production");
            builder.UseSetting("SandboxGateway:BaseUrl", "http://127.0.0.1:1");
            builder.UseSetting("SandboxGateway:AutoSpawn", "false");

            builder.ConfigureTestServices(services =>
            {
                var store = new Mock<IConversationStore>();
                _ = store
                    .Setup(s => s.LoadMetadataAsync(ThreadId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(
                        new ThreadMetadata
                        {
                            ThreadId = ThreadId,
                            LastUpdated = 0,
                            Properties = ImmutableDictionary<string, object>.Empty.Add(
                                MultiTurnAgentPool.WorkspacePropertyKey,
                                "default"
                            ),
                        }
                    );

                services.RemoveAll<IConversationStore>();
                _ = services.AddSingleton(store.Object);
                services.RemoveAll<IWorkspaceFileBrowser>();
                _ = services.AddSingleton<IWorkspaceFileBrowser>(Browser);
            });
        }
    }

    /// <summary>
    /// Mints through the REAL <c>POST .../files/grant</c> rather than calling the service, because a grant
    /// is bound to the principal that asked for it: one minted out of band carries nobody and the route
    /// answers 403 to the host's own caller. Going through the endpoint is also the only way this file sees
    /// the mint the client actually performs.
    /// </summary>
    private static async Task<string> RawUrlAsync(HttpClient client, string path)
    {
        using var minted = await client.PostAsync(
            new Uri($"/api/conversations/{ThreadId}/files/grant", UriKind.Relative),
            content: null
        );
        minted.StatusCode.Should().Be(HttpStatusCode.OK);

        var grant = (await minted.Content.ReadFromJsonAsync<WorkspaceGrantDto>())!.Grant;
        return $"/api/conversations/{ThreadId}/workspace/{grant}/{path}";
    }

    /// <summary>
    /// One COOKIE-transport mint, handing back the body and the raw <c>Set-Cookie</c> line.
    /// </summary>
    /// <remarks>
    /// The test client has no cookie container on purpose. Replaying the cookie by hand is the only way to
    /// write the case that matters — a request that presents the marker and NOTHING else — and it is also
    /// what lets these tests assert the header a browser would actually be given, attribute by attribute.
    /// </remarks>
    private static async Task<(WorkspaceGrantDto Body, string SetCookie)> MintCookieAsync(
        HttpClient client,
        string threadId = ThreadId
    )
    {
        using var minted = await client.PostAsJsonAsync(
            new Uri($"/api/conversations/{threadId}/files/grant", UriKind.Relative),
            new WorkspaceGrantRequest(WorkspaceGrantTransports.Cookie)
        );
        minted.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = (await minted.Content.ReadFromJsonAsync<WorkspaceGrantDto>())!;
        var setCookie = minted.Headers.GetValues("Set-Cookie").Should().ContainSingle().Subject;
        return (body, setCookie);
    }

    /// <summary>The <c>name=value</c> pair a browser would send back, pulled out of a <c>Set-Cookie</c> line.</summary>
    private static string CookieHeader(string setCookie) => setCookie.Split(';')[0];

    private static async Task<HttpResponseMessage> GetWithCookieAsync(HttpClient client, string url, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    // -------- Cookie transport (the grant stops being visible to the document) --------

    /// <summary>
    /// The mint's whole contract under the cookie transport: the token is in an <c>HttpOnly</c>,
    /// <c>Secure</c>, <c>SameSite=None</c>, <c>Partitioned</c>, path-scoped, DOMAIN-LESS cookie, and the body
    /// hands the client only the public marker.
    /// </summary>
    /// <remarks>
    /// Every attribute is asserted rather than the header being compared whole, because the order
    /// <c>SetCookieHeaderValue</c> writes them in is not part of any contract — while each attribute is.
    /// <c>Partitioned</c> especially: it has no <c>CookieOptions</c> property on this framework and is
    /// appended through <c>Extensions</c>, so "does it reach the wire" is a question only a real response
    /// can answer.
    /// </remarks>
    [Fact]
    public async Task Grant_OverHttp_CookieTransport_SetsTheGrantCookieAndReturnsOnlyTheMarker()
    {
        await using var host = new RawEndpointHost();
        using var client = host.CreateClient();

        var (body, setCookie) = await MintCookieAsync(client);

        body.Grant.Should().Be(WorkspaceGrantService.CookieTransportMarker);
        body.Transport.Should().Be(WorkspaceGrantTransports.Cookie);
        body.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);

        setCookie.Should().StartWith($"{WorkspaceGrantService.CookieName}=");

        // Attribute NAMES are case-insensitive on the wire and their order is nobody's contract, so the
        // line is lowered once and each attribute asserted on its own.
        var attributes = setCookie.ToLowerInvariant();
        attributes.Should().Contain("httponly");
        attributes.Should().Contain("secure");
        attributes.Should().Contain("samesite=none");
        attributes.Should().Contain("partitioned");
        attributes
            .Should()
            .Contain(
                $"path={WorkspaceGrantService.CookiePath(ThreadId).ToLowerInvariant()}",
                "the cookie must not be sent to the rest of the API"
            );
        attributes.Should().Contain($"max-age={(int)FileBrowserLimits.WorkspaceGrantLifetime.TotalSeconds}");

        // Host-only. A Domain attribute would send a live read credential to every sibling host under the
        // registrable domain, which is the one thing this transport must not do.
        attributes.Should().NotContain("domain=");

        // The marker is the ONLY thing in the URL, so it must not be the token by another name.
        setCookie
            .Should()
            .NotStartWith($"{WorkspaceGrantService.CookieName}={WorkspaceGrantService.CookieTransportMarker};");
    }

    /// <summary>
    /// The marker in the path plus the cookie on the request serves the file, exactly as the URL token did.
    /// The companion to the refusal below: without this, a route that refused everything would look correct.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_OverHttp_MarkerWithTheCookie_ServesTheFile()
    {
        await using var host = new RawEndpointHost();
        using var client = host.CreateClient();
        var (body, setCookie) = await MintCookieAsync(client);

        using var response = await GetWithCookieAsync(
            client,
            $"/api/conversations/{ThreadId}/workspace/{body.Grant}/report/index.html",
            CookieHeader(setCookie)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(IndexHtml);
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle();
    }

    /// <summary>
    /// The regression this transport exists for. A script inside the sandboxed document can read its own
    /// <c>location</c> and navigate itself anywhere — no CSP directive stops that — so whatever is in the
    /// URL is exfiltrable. Under the cookie transport that is the MARKER, and this pins that the marker on
    /// its own opens nothing: same 401 a forged token gets, and no gateway work spent reaching it.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_OverHttp_MarkerWithoutTheCookie_Is401()
    {
        await using var host = new RawEndpointHost();
        using var client = host.CreateClient();
        var (body, _) = await MintCookieAsync(client);

        using var response = await GetWithCookieAsync(
            client,
            $"/api/conversations/{ThreadId}/workspace/{body.Grant}/report/index.html",
            cookie: null
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_grant");
        host.Browser.ReadCalls.Should().Be(0);
    }

    /// <summary>
    /// A cookie minted for one conversation, replayed against another, is <c>403</c> — the token IS genuine,
    /// so re-minting it changes nothing. Moving the credential into a cookie must not weaken the thread
    /// binding that was always carried inside the token itself.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_OverHttp_CookieForAnotherConversation_Is403()
    {
        await using var host = new RawEndpointHost();
        using var client = host.CreateClient();
        var (_, setCookie) = await MintCookieAsync(client);

        using var response = await GetWithCookieAsync(
            client,
            $"/api/conversations/other/workspace/{WorkspaceGrantService.CookieTransportMarker}/report/index.html",
            CookieHeader(setCookie)
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("grant_thread_mismatch");
    }

    /// <summary>
    /// The fallback every pre-cookie caller already sends: a <c>POST</c> with NO body at all still mints the
    /// URL-transport token, and the route still serves it with no cookie anywhere.
    /// </summary>
    [Fact]
    public async Task Grant_OverHttp_WithNoBody_StillMintsTheUrlTransport()
    {
        await using var host = new RawEndpointHost();
        using var client = host.CreateClient();

        using var minted = await client.PostAsync(
            new Uri($"/api/conversations/{ThreadId}/files/grant", UriKind.Relative),
            content: null
        );

        minted.StatusCode.Should().Be(HttpStatusCode.OK);
        minted.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse();

        var body = (await minted.Content.ReadFromJsonAsync<WorkspaceGrantDto>())!;
        body.Transport.Should().Be(WorkspaceGrantTransports.Url);
        body.Grant.Should().NotBe(WorkspaceGrantService.CookieTransportMarker);

        using var served = await client.GetAsync(
            new Uri($"/api/conversations/{ThreadId}/workspace/{body.Grant}/report/index.html", UriKind.Relative)
        );
        served.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The whole response, over the wire: the mapped media type, every security header, and a
    /// <c>Content-Disposition</c> that survived the file result executing. A header written onto
    /// <c>Response</c> before a <c>FileContentResult</c> runs is exactly the kind of thing a direct action
    /// call cannot vouch for.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_OverHttp_ServesTheBytesWithEverySecurityHeader()
    {
        await using var host = new RawEndpointHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(
            new Uri(await RawUrlAsync(client, "report/index.html"), UriKind.Relative)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(IndexHtml);

        response.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.CacheControl!.ToString().Should().Contain("no-store");
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");

        var csp = response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Subject;
        csp.Should().Be(WorkspaceContentTypes.SandboxPolicy);
        csp.Should().NotContain("allow-same-origin");
        csp.Should().NotContain("allow-popups");

        var disposition = response.Content.Headers.ContentDisposition!;
        disposition.DispositionType.Should().Be("inline");
        disposition.FileName.Should().Contain("index.html");
    }

    [Fact]
    public async Task RawWorkspaceFile_OverHttp_WithDownloadQuery_IsAnAttachment()
    {
        await using var host = new RawEndpointHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(
            new Uri($"{await RawUrlAsync(client, "report/index.html")}?download=1", UriKind.Relative)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
    }

    /// <summary>
    /// The refusal of <c>..</c> has to hold against the PERCENT-ENCODED spelling, which is the only one a
    /// caller trying to traverse would send — routing decodes it, so a guard that matched the literal two
    /// dots in the raw URL would never see it. This is the case the direct-action tests structurally cannot
    /// reach, because they are handed an already-decoded string.
    /// </summary>
    [Theory]
    [InlineData("report/%2e%2e/secret.txt")]
    [InlineData("%2e%2e/secret.txt")]
    [InlineData("report/../secret.txt")]
    public async Task RawWorkspaceFile_OverHttp_EncodedTraversal_IsRefused(string path)
    {
        await using var host = new RawEndpointHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri(await RawUrlAsync(client, path), UriKind.Relative));

        ((int)response.StatusCode).Should().BeInRange(400, 499);
        // Whatever the refusal is, it must NOT be the file: a 2xx here is the traversal succeeding.
        response.IsSuccessStatusCode.Should().BeFalse();
    }

    /// <summary>
    /// A forged grant is refused by the route itself even with enforcement off, where no identity middleware
    /// would have stopped it. The grant is the only thing standing between a guessed conversation id and its
    /// workspace on this route.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_OverHttp_ForgedGrant_Is401()
    {
        await using var host = new RawEndpointHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(
            new Uri($"/api/conversations/{ThreadId}/workspace/not-a-real-grant/report/index.html", UriKind.Relative)
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
