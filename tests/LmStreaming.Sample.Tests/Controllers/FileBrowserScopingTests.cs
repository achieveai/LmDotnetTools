using System.Collections.Immutable;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.FileBrowser;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Pins <see cref="FileBrowserController"/> against an authenticated user of one tenant actively trying to
/// read, download, preview, write, create and delete inside ANOTHER tenant's conversation workspace.
/// </summary>
/// <remarks>
/// <para>
/// The controller addresses a workspace by conversation id alone, so without a per-request authorization
/// check its six routes are a sibling of <c>ConversationsController</c>'s that answers the very same ids
/// with the very same workspace — and the conversation isolation the other controller enforces would be
/// reachable around, not through. Every route is covered separately because each carries its own
/// <c>[Http*]</c> attribute: a single test on <c>List</c> would leave exactly the per-route hole that
/// produced this class.
/// </para>
/// <para>
/// Each refusal is asserted BYTE-IDENTICAL against the response the same route gives for a thread id that
/// was never minted, with the id substituted. Anything else — a distinct code, a 403, a different field
/// order — turns the route into an existence oracle for another tenant's conversation ids.
/// </para>
/// <para>
/// The sandbox double is asserted untouched on every refusal. That is what separates "refused" from
/// "refused after listing/reading/writing it", and it is also the timing argument: the forbidden case does
/// exactly the same work as the unknown case, which is none.
/// </para>
/// </remarks>
public sealed class FileBrowserScopingTests
{
    private const string AliceThread = "alice-thread";
    private const string MissingThread = "no-such-thread";
    private const string TenantA = "tnt_a";
    private const string TenantB = "tnt_b";
    private const string Alice = "dir-a:alice";
    private const string Mallory = "dir-b:mallory";
    private const string SecretFile = "secret.txt";

    private readonly InMemoryConversationStore _store = new();
    private readonly InMemoryResourceGrantStore _grants = new();

    private static Principal Signed(string tenantId, string userId) =>
        new()
        {
            TenantId = tenantId,
            Actor = new PrincipalRef(PrincipalKind.EndUser, userId),
            Roles = new HashSet<string>(StringComparer.Ordinal),
            Source = PrincipalSource.Interactive,
        };

    private sealed class FakeFormFile(string fileName, byte[] content) : IFormFile
    {
        private readonly byte[] _content = content;

        public string ContentType { get; set; } = "application/octet-stream";
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length { get; } = content.Length;
        public string Name { get; set; } = "file";
        public string FileName { get; } = fileName;

        public void CopyTo(Stream target) => target.Write(_content, 0, _content.Length);

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            target.WriteAsync(_content, cancellationToken).AsTask();

        public Stream OpenReadStream() => new MemoryStream(_content);
    }

    private async Task SeedAliceThreadAsync() =>
        await _store.UpdateMetadataAsync(
            AliceThread,
            existing => new ThreadMetadata
            {
                ThreadId = AliceThread,
                LastUpdated = 1_000,
                Properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty)
                    .SetItem(MultiTurnAgentPool.WorkspacePropertyKey, "alice-workspace"),
                TenantId = TenantA,
                OwnerUserId = Alice,
                Visibility = Visibility.Private,
            },
            CancellationToken.None);

    /// <summary>
    /// Builds the controller acting as <paramref name="principal"/> with enforcement ON, over a workspace
    /// whose root holds one file.
    /// </summary>
    private (FileBrowserController Controller, FakeFileBrowser Browser) Build(Principal principal)
    {
        var browser = new FakeFileBrowser
        {
            FileBytes = Encoding.UTF8.GetBytes("alice's private notes"),
        };
        browser.Listings[string.Empty] = [new(SecretFile, SandboxEntryType.File, 21, false)];

        var controller = new FileBrowserController(
            _store,
            browser,
            TestAuthorizers.Enforcing(principal, _grants),
            NullLogger<FileBrowserController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, browser);
    }

    /// <summary>The sandbox seam saw nothing at all: no listing, no read, no write, no command.</summary>
    private static void AssertSandboxUntouched(FakeFileBrowser browser)
    {
        _ = browser.LastPersistedWorkspaceId.Should().BeNull();
        _ = browser.ReadCalls.Should().Be(0);
        _ = browser.Writes.Should().BeEmpty();
        _ = browser.Commands.Should().BeEmpty();
    }

    /// <summary>
    /// Asserts the two refusals are the same bytes once the ids are made equal, which is the only form of
    /// "indistinguishable" a caller can actually check.
    /// </summary>
    private static void AssertIndistinguishable(IActionResult forbidden, IActionResult unknown)
    {
        var refused = Assert.IsType<NotFoundObjectResult>(forbidden);
        var missing = Assert.IsType<NotFoundObjectResult>(unknown);

        _ = refused.StatusCode.Should().Be(404);
        _ = JsonSerializer.Serialize(refused.Value)
            .Should().Be(
                JsonSerializer.Serialize(missing.Value)
                    .Replace(MissingThread, AliceThread, StringComparison.Ordinal));
    }

    // -------- Non-vacuity: the same fixture answers the OWNER --------

    /// <summary>
    /// The owner reaches every route this class refuses for a stranger. Without this the six refusals below
    /// would pass just as well against a fixture that cannot reach the workspace for any caller at all.
    /// </summary>
    [Fact]
    public async Task Owner_ReachesEveryRoute_SoTheRefusalsBelowAreNotVacuous()
    {
        await SeedAliceThreadAsync();
        var (controller, browser) = Build(Signed(TenantA, Alice));

        var listing = Assert.IsType<OkObjectResult>(
            await controller.List(AliceThread, path: null, CancellationToken.None));
        _ = Assert.IsType<DirectoryListingDto>(listing.Value).Entries
            .Select(e => e.Name).Should().Contain(SecretFile);

        _ = Assert.IsType<FileContentResult>(
            await controller.Download(AliceThread, SecretFile, CancellationToken.None));

        var preview = Assert.IsType<OkObjectResult>(
            await controller.Preview(AliceThread, SecretFile, CancellationToken.None));
        _ = Assert.IsType<PreviewResultDto>(preview.Value).Previewable.Should().BeTrue();

        _ = Assert.IsType<OkObjectResult>(
            await controller.Upload(AliceThread, path: null, new FakeFormFile("added.txt", [1, 2, 3]), relativePath: null, CancellationToken.None));

        _ = Assert.IsType<OkObjectResult>(
            await controller.CreateDirectory(AliceThread, path: null, new CreateDirectoryRequest("newdir"), CancellationToken.None));

        _ = Assert.IsType<NoContentResult>(
            await controller.Delete(AliceThread, SecretFile, CancellationToken.None));

        _ = browser.Writes.Should().ContainSingle();
        _ = browser.Commands.Should().HaveCount(2);
    }

    // -------- Read routes --------

    /// <summary>
    /// The disclosure this class exists for: a listing of another tenant's workspace, returned by id alone.
    /// </summary>
    [Fact]
    public async Task CrossTenantList_IsIndistinguishableFromAThreadThatDoesNotExist()
    {
        await SeedAliceThreadAsync();
        var (controller, browser) = Build(Signed(TenantB, Mallory));

        var crossTenant = await controller.List(AliceThread, path: null, CancellationToken.None);
        var neverExisted = await controller.List(MissingThread, path: null, CancellationToken.None);

        AssertIndistinguishable(crossTenant, neverExisted);
        AssertSandboxUntouched(browser);
    }

    /// <summary>A cross-tenant download is refused, and no byte of the file is read to refuse it.</summary>
    [Fact]
    public async Task CrossTenantDownload_IsIndistinguishableFromAThreadThatDoesNotExist()
    {
        await SeedAliceThreadAsync();
        var (controller, browser) = Build(Signed(TenantB, Mallory));

        var crossTenant = await controller.Download(AliceThread, SecretFile, CancellationToken.None);
        var neverExisted = await controller.Download(MissingThread, SecretFile, CancellationToken.None);

        AssertIndistinguishable(crossTenant, neverExisted);
        AssertSandboxUntouched(browser);
    }

    /// <summary>A cross-tenant preview is refused; the file's text never leaves the workspace.</summary>
    [Fact]
    public async Task CrossTenantPreview_IsIndistinguishableFromAThreadThatDoesNotExist()
    {
        await SeedAliceThreadAsync();
        var (controller, browser) = Build(Signed(TenantB, Mallory));

        var crossTenant = await controller.Preview(AliceThread, SecretFile, CancellationToken.None);
        var neverExisted = await controller.Preview(MissingThread, SecretFile, CancellationToken.None);

        AssertIndistinguishable(crossTenant, neverExisted);
        AssertSandboxUntouched(browser);
    }

    // -------- Write routes --------

    /// <summary>A cross-tenant upload is refused AND writes nothing.</summary>
    [Fact]
    public async Task CrossTenantUpload_IsIndistinguishableFromAThreadThatDoesNotExist()
    {
        await SeedAliceThreadAsync();
        var (controller, browser) = Build(Signed(TenantB, Mallory));

        var crossTenant = await controller.Upload(
            AliceThread, path: null, new FakeFormFile("planted.txt", [1, 2, 3]), relativePath: null, CancellationToken.None);
        var neverExisted = await controller.Upload(
            MissingThread, path: null, new FakeFormFile("planted.txt", [1, 2, 3]), relativePath: null, CancellationToken.None);

        AssertIndistinguishable(crossTenant, neverExisted);
        AssertSandboxUntouched(browser);
    }

    /// <summary>A cross-tenant mkdir is refused AND runs no command.</summary>
    [Fact]
    public async Task CrossTenantCreateDirectory_IsIndistinguishableFromAThreadThatDoesNotExist()
    {
        await SeedAliceThreadAsync();
        var (controller, browser) = Build(Signed(TenantB, Mallory));

        var crossTenant = await controller.CreateDirectory(
            AliceThread, path: null, new CreateDirectoryRequest("planted"), CancellationToken.None);
        var neverExisted = await controller.CreateDirectory(
            MissingThread, path: null, new CreateDirectoryRequest("planted"), CancellationToken.None);

        AssertIndistinguishable(crossTenant, neverExisted);
        AssertSandboxUntouched(browser);
    }

    /// <summary>
    /// The most damaging call this controller answers by id alone. A cross-tenant delete is refused AND no
    /// <c>rm</c> reaches the sandbox — the assertion on the command log is what separates "refused" from
    /// "refused after doing it".
    /// </summary>
    [Fact]
    public async Task CrossTenantDelete_IsIndistinguishableFromAThreadThatDoesNotExist()
    {
        await SeedAliceThreadAsync();
        var (controller, browser) = Build(Signed(TenantB, Mallory));

        var crossTenant = await controller.Delete(AliceThread, SecretFile, CancellationToken.None);
        var neverExisted = await controller.Delete(MissingThread, SecretFile, CancellationToken.None);

        AssertIndistinguishable(crossTenant, neverExisted);
        AssertSandboxUntouched(browser);
    }

    // -------- The boundary is the owner, not the tenant --------

    /// <summary>
    /// A tenant-MATE with no grant is refused the same way. Same tenant is not the boundary; the owner is.
    /// This is also the case that separates the owner check from the tenant conjunct — a cross-tenant caller
    /// is refused by either one alone.
    /// </summary>
    [Fact]
    public async Task TenantMateWithoutAGrant_IsRefusedAsUnknown()
    {
        await SeedAliceThreadAsync();
        var (controller, browser) = Build(Signed(TenantA, "dir-a:bob"));

        var result = await controller.List(AliceThread, path: null, CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result).StatusCode.Should().Be(404);
        AssertSandboxUntouched(browser);
    }

    // -------- The refusal must cost the same lookup WORK, not just the same bytes --------

    /// <summary>
    /// Byte-identical is not the whole oracle. A forbidden cross-tenant thread runs the authorizer's
    /// equalising grant lookup; a thread that was never minted must run the SAME lookup, or the count of
    /// store round-trips distinguishes the two even when their bodies do not. The defect this pins:
    /// <c>ResolveSessionAsync</c> answered the null-metadata case with <c>unknown_thread</c> BEFORE
    /// reaching <c>AuthorizeAsync</c>, so a missing thread cost zero lookups while a forbidden one cost one
    /// — a #389 work-shape existence oracle at the controller seam that the body-only tests above cannot
    /// see.
    /// </summary>
    [Fact]
    public async Task CrossTenantList_AndAThreadThatDoesNotExist_CostTheSameGrantLookups()
    {
        await SeedAliceThreadAsync();

        var forbidden = await CountListLookupsAsync(Signed(TenantB, Mallory), AliceThread);
        var missing = await CountListLookupsAsync(Signed(TenantB, Mallory), MissingThread);

        _ = forbidden.Should().BeGreaterThan(
            0, "a cross-tenant listing runs the authorizer's equalising grant lookup");
        _ = missing.Should().Be(
            forbidden,
            "a thread that was never minted must cost the same grant-lookup work, or the round-trip count is an existence oracle");
    }

    /// <summary>
    /// Lists <paramref name="threadId"/> as <paramref name="principal"/> over a fresh counting grant store,
    /// and returns how many grant look-ups the request made.
    /// </summary>
    private async Task<int> CountListLookupsAsync(Principal principal, string threadId)
    {
        var grants = new CountingResourceGrantStore(_grants);
        var browser = new FakeFileBrowser
        {
            FileBytes = Encoding.UTF8.GetBytes("alice's private notes"),
        };
        browser.Listings[string.Empty] = [new(SecretFile, SandboxEntryType.File, 21, false)];

        var controller = new FileBrowserController(
            _store,
            browser,
            TestAuthorizers.Enforcing(principal, grants),
            NullLogger<FileBrowserController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _ = await controller.List(threadId, path: null, CancellationToken.None);
        return grants.FindGrantCallCount;
    }
}
