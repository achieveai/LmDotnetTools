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
