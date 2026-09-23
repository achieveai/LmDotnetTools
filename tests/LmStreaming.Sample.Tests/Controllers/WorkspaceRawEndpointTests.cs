using System.Collections.Immutable;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.FileBrowser;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Covers the Bug#15 PATH-addressed raw workspace route
/// (<c>GET /api/conversations/{threadId}/workspace/{grant}/{**path}</c>) and the
/// <c>POST .../files/grant</c> that mints its credential.
/// </summary>
/// <remarks>
/// The reason this route exists is that a document served from it keeps its own relative links working, and
/// the reason it is SAFE is a set of response headers. Both halves are asserted here: the media type comes
/// from a closed map, and every response carries <c>nosniff</c>, <c>no-store</c> and <c>no-referrer</c>,
/// while an executable document additionally carries a CSP whose <c>sandbox</c> directive never contains
/// <c>allow-same-origin</c> — the one token that would hand untrusted workspace HTML this app's origin.
/// </remarks>
public class WorkspaceRawEndpointTests
{
    private const string ThreadId = "t1";

    private static (FileBrowserController Controller, FakeFileBrowser Browser, WorkspaceGrantService Grants) Build(
        FakeTimeProvider? time = null
    )
    {
        var store = new Mock<IConversationStore>();
        store
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

        var browser = new FakeFileBrowser();
        var controller = new FileBrowserController(
            store.Object,
            browser,
            TestAuthorizers.Disabled(),
            NullLogger<FileBrowserController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var grants = new WorkspaceGrantService(
            new EphemeralDataProtectionProvider(),
            time ?? new FakeTimeProvider(DateTimeOffset.UtcNow)
        );
        return (controller, browser, grants);
    }

    private static SandboxDirectoryEntry File(string name, long? size = 10) =>
        new(name, SandboxEntryType.File, size, false);

    private static SandboxDirectoryEntry Dir(string name) => new(name, SandboxEntryType.Directory, null, false);

    /// <summary>
    /// The nested workspace tree every path test resolves against:
    /// <c>report/index.html</c>, <c>report/img/dot.png</c>, <c>.conversations/x.jsonl</c>.
    /// </summary>
    private static void SeedNestedTree(FakeFileBrowser browser)
    {
        browser.Listings[""] = [Dir("report"), Dir(".conversations"), File("top.txt")];
        browser.Listings["report"] = [File("index.html", 120), Dir("img")];
        browser.Listings["report/img"] = [File("dot.png", 70)];
        browser.Listings[".conversations"] = [File("x.jsonl", 50)];
    }

    private static Task<IActionResult> Get(
        FileBrowserController controller,
        WorkspaceGrantService grants,
        string path,
        string? download = null,
        string? token = null
    ) =>
        controller.RawWorkspaceFile(
            ThreadId,
            token ?? grants.Mint(ThreadId, null).Token,
            path,
            download,
            grants,
            CancellationToken.None
        );

    // -------- Grant minting --------

    [Fact]
    public async Task Grant_ReturnsATokenThatValidatesForTheSameThread()
    {
        var (controller, _, grants) = Build();

        var result = await controller.Grant(ThreadId, grants, CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<WorkspaceGrantDto>().Which;
        grants.Validate(dto.Grant, ThreadId, null).Should().Be(WorkspaceGrantFailure.None);
    }

    [Fact]
    public async Task Grant_ReportsTheExpiryTheClientRefreshesAgainst()
    {
        var now = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var (controller, _, grants) = Build(new FakeTimeProvider(now));

        var result = await controller.Grant(ThreadId, grants, CancellationToken.None);

        result
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeOfType<WorkspaceGrantDto>()
            .Which.ExpiresAt.Should()
            .Be(now + FileBrowserLimits.WorkspaceGrantLifetime);
    }

    /// <summary>
    /// A grant is only useful against a live sandbox session, and minting one for a conversation that has
    /// none would hand out a credential that addresses nothing — so the read prologue's 409 wins.
    /// </summary>
    [Fact]
    public async Task Grant_NoSandboxSession_Returns409()
    {
        var (controller, browser, grants) = Build();
        browser.Resolution = new SandboxSessionResolution(SandboxSessionResolutionOutcome.NoSession, null, null, null);

        var result = await controller.Grant(ThreadId, grants, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    // -------- Grant enforcement on the raw route --------

    [Fact]
    public async Task RawWorkspaceFile_UnsignedToken_Returns401AndTouchesNoGateway()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);

        var result = await Get(controller, grants, "report/index.html", token: "not-a-grant");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        // The grant is checked before the session prologue, so a forged URL costs no gateway work at all.
        browser.ResolveCredentials.Should().BeEmpty();
        browser.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task RawWorkspaceFile_GrantMintedForAnotherThread_Returns403()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);
        var foreign = grants.Mint("another-thread", null).Token;

        var result = await Get(controller, grants, "report/index.html", token: foreign);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        browser.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task RawWorkspaceFile_ExpiredGrant_Returns401()
    {
        var expiredClock = new FakeTimeProvider(DateTimeOffset.UtcNow - (FileBrowserLimits.WorkspaceGrantLifetime * 2));
        var (controller, browser, _) = Build();
        SeedNestedTree(browser);
        var stale = new WorkspaceGrantService(new EphemeralDataProtectionProvider(), expiredClock);

        var result = await Get(controller, stale, "report/index.html");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // -------- Serving a file --------

    [Fact]
    public async Task RawWorkspaceFile_NestedFile_ServesItsBytesWithTheMappedContentType()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);
        browser.FileBytes = Encoding.UTF8.GetBytes("<html><img src=\"img/dot.png\"></html>");

        var result = await Get(controller, grants, "report/index.html");

        var file = result.Should().BeOfType<FileContentResult>().Which;
        file.ContentType.Should().Be("text/html; charset=utf-8");
        Encoding.UTF8.GetString(file.FileContents).Should().Contain("img/dot.png");
    }

    [Fact]
    public async Task RawWorkspaceFile_NestedSubresource_ResolvesThroughTheSameComponentWiseWalk()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);
        browser.FileBytes = [0x89, 0x50, 0x4E, 0x47];

        var result = await Get(controller, grants, "report/img/dot.png");

        result.Should().BeOfType<FileContentResult>().Which.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task RawWorkspaceFile_UnknownExtension_IsServedAsOctetStream()
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File("tool.exe")];

        var result = await Get(controller, grants, "tool.exe");

        result.Should().BeOfType<FileContentResult>().Which.ContentType.Should().Be(WorkspaceContentTypes.Default);
    }

    // -------- Headers --------

    [Fact]
    public async Task RawWorkspaceFile_AlwaysSetsNosniffNoStoreAndNoReferrer()
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File("top.txt")];

        _ = await Get(controller, grants, "top.txt");

        var headers = controller.Response.Headers;
        headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        headers.CacheControl.ToString().Should().Be("private, no-store");
        headers["Referrer-Policy"].ToString().Should().Be("no-referrer");
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("page.xhtml")]
    [InlineData("logo.svg")]
    public async Task RawWorkspaceFile_ExecutableDocument_CarriesTheSandboxCsp(string name)
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File(name)];

        _ = await Get(controller, grants, name);

        var csp = controller.Response.Headers.ContentSecurityPolicy.ToString();
        csp.Should().StartWith("sandbox ");
        csp.Should().Contain("frame-ancestors 'self'");
    }

    /// <summary>
    /// The one token that must never appear. With <c>allow-same-origin</c> the sandboxed document would run
    /// in THIS app's origin and could read its cookies, <c>localStorage</c> and bearer token, and call
    /// <c>/api/*</c> as the signed-in user — which is the entire risk this feature exists to avoid.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_SandboxCsp_NeverGrantsSameOrigin()
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File("index.html")];

        _ = await Get(controller, grants, "index.html");

        controller.Response.Headers.ContentSecurityPolicy.ToString().Should().NotContain("allow-same-origin");
    }

    /// <summary>
    /// <c>allow-popups</c> is absent too, and for a different reason from <c>allow-same-origin</c>: the
    /// document's own URL carries the grant, so <c>window.open</c> to an off-origin URL would hand a live
    /// read credential to another host — and a top-level navigation is something no directive in this policy
    /// can stop. <c>allow-scripts</c> stays, because a report that cannot run its own chart script is not a
    /// preview of that report.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_SandboxCsp_NeverGrantsPopups()
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File("index.html")];

        _ = await Get(controller, grants, "index.html");

        var csp = controller.Response.Headers.ContentSecurityPolicy.ToString();
        csp.Should().NotContain("allow-popups");
        csp.Should().Contain("allow-scripts");
    }

    /// <summary>
    /// An inert type gets no CSP. Not an oversight: a sandbox directive on a subresource response neither
    /// travels to the document that fetched it nor restricts anything on its own, and attaching one to every
    /// image would make the header meaningless as a signal.
    /// </summary>
    [Theory]
    [InlineData("dot.png")]
    [InlineData("top.txt")]
    [InlineData("style.css")]
    [InlineData("app.js")]
    public async Task RawWorkspaceFile_InertType_CarriesNoCsp(string name)
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File(name)];

        _ = await Get(controller, grants, name);

        controller.Response.Headers.ContentSecurityPolicy.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task RawWorkspaceFile_ByDefault_IsServedInline()
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File("top.txt")];

        _ = await Get(controller, grants, "top.txt");

        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline");
    }

    [Fact]
    public async Task RawWorkspaceFile_WithDownloadQuery_IsServedAsAnAttachmentNamedAfterTheFile()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);

        _ = await Get(controller, grants, "report/img/dot.png", download: "1");

        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        disposition.Should().StartWith("attachment");
        disposition.Should().Contain("dot.png");
    }

    // -------- Refusals --------

    [Fact]
    public async Task RawWorkspaceFile_Directory_Returns404NotAFile()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);

        var result = await Get(controller, grants, "report");

        result.Should().BeOfType<NotFoundObjectResult>();
        browser.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task RawWorkspaceFile_Symlink_Returns404RatherThanFollowingIt()
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [new SandboxDirectoryEntry("link", SandboxEntryType.Symlink, null, false)];

        var result = await Get(controller, grants, "link");

        result.Should().BeOfType<NotFoundObjectResult>();
        browser.ReadCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("report/../report/index.html")]
    [InlineData("./report/index.html")]
    [InlineData("report\\index.html")]
    public async Task RawWorkspaceFile_DotOrBackslashSegment_Returns400WithoutReading(string path)
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);

        var result = await Get(controller, grants, path);

        result.Should().BeOfType<BadRequestObjectResult>();
        browser.ReadCalls.Should().Be(0);
    }

    /// <summary>
    /// A file under a dot-directory is refused even though its extension is harmless: the workspace
    /// transcript mirror (#251) writes the conversation's own messages into <c>.conversations/*.jsonl</c>,
    /// and this route is reachable from inside a rendered document.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_UnderDotDirectory_Returns404WithoutReading()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);

        var result = await Get(controller, grants, ".conversations/x.jsonl");

        result.Should().BeOfType<NotFoundObjectResult>();
        browser.ReadCalls.Should().Be(0);
    }

    /// <summary>A dot-FILE at the top level stays readable — only dot-DIRECTORIES are machine-owned.</summary>
    [Fact]
    public async Task RawWorkspaceFile_TopLevelDotFile_IsStillServed()
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File(".gitignore")];

        var result = await Get(controller, grants, ".gitignore");

        result.Should().BeOfType<FileContentResult>();
    }

    [Fact]
    public async Task RawWorkspaceFile_ListedSizeOverCap_Returns413WithoutReadingAByte()
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File("huge.bin", FileBrowserLimits.MaxDownloadBytes + 1)];

        var result = await Get(controller, grants, "huge.bin");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        browser.ReadCalls.Should().Be(0);
    }

    /// <summary>
    /// A file whose listed size was absent but whose bytes stream past the cap: the SDK's direct-read
    /// refusal becomes the same 413, not an opaque gateway error.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_StreamedBytesOverCap_Returns413()
    {
        var (controller, browser, grants) = Build();
        browser.Listings[""] = [File("huge.bin", size: null)];
        browser.ReadThrows = new SandboxException(SandboxErrorKind.Protocol, "exceeded the direct-read cap")
        {
            IsDirectReadCapExceeded = true,
        };

        var result = await Get(controller, grants, "huge.bin");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task RawWorkspaceFile_MissingFile_Returns404()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);

        var result = await Get(controller, grants, "report/missing.html");

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    /// <summary>
    /// A valid grant does not skip authorization. The read prologue still runs, so a conversation whose
    /// sandbox session has gone answers its ordinary 409 rather than serving bytes.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_ValidGrantButNoSession_StillRunsTheReadPrologue()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);
        browser.Resolution = new SandboxSessionResolution(SandboxSessionResolutionOutcome.NoSession, null, null, null);

        var result = await Get(controller, grants, "report/index.html");

        result.Should().BeOfType<ConflictObjectResult>();
        browser.ReadCalls.Should().Be(0);
    }

    // -------- The grant is bound to the caller it was minted for --------

    private static Principal PrincipalNamed(string id) =>
        new()
        {
            TenantId = "tnt_a",
            Actor = new PrincipalRef(PrincipalKind.EndUser, id),
            Source = PrincipalSource.Interactive,
        };

    /// <summary>
    /// A grant minted for one caller, presented by another, is refused at the controller — the grant is a
    /// bearer credential, so a leaked one (browser history, a shared link, a proxy log) must not become a
    /// second caller's read access to the first caller's workspace.
    /// </summary>
    /// <remarks>
    /// <c>403</c> rather than <c>401</c>: nothing is wrong with the token, and the caller IS authenticated,
    /// so there is nothing for a refresh to fix. <see cref="Identity.WorkspaceGrantPrincipalSourceTests"/>
    /// covers the enforcing-host door; this covers what the ACTION does once a principal is on the context,
    /// which is the check that still applies when some other front door established it.
    /// </remarks>
    [Fact]
    public async Task RawWorkspaceFile_GrantMintedForAnotherPrincipal_Is403()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);
        var minted = grants.Mint(ThreadId, PrincipalNamed("tnt_a:oid_owner")).Token;
        controller.HttpContext.Items[IdentityHttpItems.PrincipalKey] = PrincipalNamed("tnt_a:oid_intruder");

        var result = await Get(controller, grants, "report/index.html", token: minted);

        var refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        refusal.Value.Should().NotBeNull();
        System.Text.Json.JsonSerializer.Serialize(refusal.Value).Should().Contain("grant_principal_mismatch");
        browser.ReadCalls.Should().Be(0);
    }

    /// <summary>
    /// The companion that keeps the case above from passing for the wrong reason: the SAME principal is
    /// served. Without this, a check that refused every authenticated caller would look correct.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_GrantMintedForTheSamePrincipal_IsServed()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);
        var minted = grants.Mint(ThreadId, PrincipalNamed("tnt_a:oid_owner")).Token;
        controller.HttpContext.Items[IdentityHttpItems.PrincipalKey] = PrincipalNamed("tnt_a:oid_owner");

        var result = await Get(controller, grants, "report/index.html", token: minted);

        result.Should().BeOfType<FileContentResult>();
    }

    // -------- The same two answers when the token travels in the cookie --------

    /// <summary>
    /// Puts the token where a browser would put it under the cookie transport, so the URL segment the
    /// action sees is only the public marker.
    /// </summary>
    private static void PresentGrantCookie(FileBrowserController controller, string token) =>
        controller.HttpContext.Request.Headers.Cookie = $"{WorkspaceGrantService.CookieName}={token}";

    /// <summary>
    /// The principal binding is a property of the TOKEN, not of where it travelled — so it has to hold
    /// identically once the token is in a cookie. Without this pair the cookie transport could quietly have
    /// become a way around the check the URL transport enforces.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_CookieMintedForAnotherPrincipal_Is403()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);
        PresentGrantCookie(controller, grants.Mint(ThreadId, PrincipalNamed("tnt_a:oid_owner")).Token);
        controller.HttpContext.Items[IdentityHttpItems.PrincipalKey] = PrincipalNamed("tnt_a:oid_intruder");

        var result = await Get(
            controller,
            grants,
            "report/index.html",
            token: WorkspaceGrantService.CookieTransportMarker
        );

        var refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        System.Text.Json.JsonSerializer.Serialize(refusal.Value).Should().Contain("grant_principal_mismatch");
        browser.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task RawWorkspaceFile_CookieMintedForTheSamePrincipal_IsServed()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);
        PresentGrantCookie(controller, grants.Mint(ThreadId, PrincipalNamed("tnt_a:oid_owner")).Token);
        controller.HttpContext.Items[IdentityHttpItems.PrincipalKey] = PrincipalNamed("tnt_a:oid_owner");

        var result = await Get(
            controller,
            grants,
            "report/index.html",
            token: WorkspaceGrantService.CookieTransportMarker
        );

        result.Should().BeOfType<FileContentResult>();
    }

    /// <summary>
    /// The marker is a public constant, so it must not be a credential. Presented with no cookie behind it
    /// — which is all a script inside the sandboxed document can read off its own <c>location</c> — it is
    /// refused as an unreadable token, before any gateway work.
    /// </summary>
    [Fact]
    public async Task RawWorkspaceFile_MarkerWithNoCookie_Is401AndReadsNothing()
    {
        var (controller, browser, grants) = Build();
        SeedNestedTree(browser);

        var result = await Get(
            controller,
            grants,
            "report/index.html",
            token: WorkspaceGrantService.CookieTransportMarker
        );

        var refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        System.Text.Json.JsonSerializer.Serialize(refusal.Value).Should().Contain("invalid_grant");
        browser.ReadCalls.Should().Be(0);
    }

    // -------- Which transport the mint answers with --------

    /// <summary>
    /// A mint that asks for the cookie transport hands the client the MARKER and nothing else, and puts the
    /// real token in the response's cookies. The header's own attributes are pinned over real HTTP in
    /// <c>WorkspaceRawEndpointHttpTests</c>, which is the only place they reach a wire.
    /// </summary>
    [Fact]
    public async Task Grant_CookieTransport_ReturnsTheMarkerAndPutsTheTokenInTheCookie()
    {
        var (controller, _, grants) = Build();

        var result = await controller.Grant(
            ThreadId,
            grants,
            CancellationToken.None,
            new WorkspaceGrantRequest(WorkspaceGrantTransports.Cookie)
        );

        var dto = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<WorkspaceGrantDto>().Which;
        dto.Grant.Should().Be(WorkspaceGrantService.CookieTransportMarker);
        dto.Transport.Should().Be(WorkspaceGrantTransports.Cookie);

        var setCookie = controller.Response.Headers.SetCookie.ToString();
        setCookie.Should().StartWith($"{WorkspaceGrantService.CookieName}=");
        grants
            .Validate(setCookie.Split(';')[0].Split('=', 2)[1], ThreadId, null)
            .Should()
            .Be(WorkspaceGrantFailure.None);
    }

    /// <summary>
    /// Anything that is not the cookie transport - an absent body, an older client, a value this build does
    /// not know - keeps the URL token it always got. Degrading to a 400 would break every caller written
    /// before the cookie existed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("url")]
    [InlineData("something-else")]
    public async Task Grant_AnythingButTheCookieTransport_KeepsTheUrlToken(string? transport)
    {
        var (controller, _, grants) = Build();

        var result = await controller.Grant(
            ThreadId,
            grants,
            CancellationToken.None,
            transport is null ? null : new WorkspaceGrantRequest(transport)
        );

        var dto = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<WorkspaceGrantDto>().Which;
        dto.Transport.Should().Be(WorkspaceGrantTransports.Url);
        dto.Grant.Should().NotBe(WorkspaceGrantService.CookieTransportMarker);
        grants.Validate(dto.Grant, ThreadId, null).Should().Be(WorkspaceGrantFailure.None);
        controller.Response.Headers.SetCookie.ToString().Should().BeEmpty();
    }
}
