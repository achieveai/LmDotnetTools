using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using LmStreaming.Sample.Services.Discovery;

namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// Pins the direct <see cref="WorkspaceSubAgentLoader.LoadOneAsync"/> contract used by the
/// <see cref="LmStreaming.Sample.Controllers.ContextDiscoveryController"/> webhook handler:
/// a single discovered item resolves to either a fully-mapped template (happy path) or null
/// (anything skippable — wrong kind, traversal, missing file, malformed markdown). Cancellation
/// propagates; null args throw. The webhook handler relies on uniform null-on-failure so it can
/// log + 200 without branching on each failure mode.
/// </summary>
public class WorkspaceSubAgentLoaderLoadOneAsyncTests
{
    private const string SessionId = "session-load-one-test";
    private const string GatewayBaseUrl = "http://localhost:3000";

    private static readonly Mock<IStreamingAgent> AgentStub = new();
    private static readonly Func<IStreamingAgent> AgentFactory = () => AgentStub.Object;

    private static readonly string WellFormedMarkdown = """
        ---
        name: echo
        description: Echoes a marker.
        ---
        You are the echo sub-agent. Respond with the marker the user supplies.
        """;

    private SandboxSession CreateSession(string? hostPathOverride = null) =>
        new(
            WorkspaceId: "default",
            SessionId: SessionId,
            WorkspaceRelPath: "default",
            HostPath: hostPathOverride ?? "/workspace"
        );

    /// <summary>
    /// A loader over a registry whose gateway answers with <paramref name="respond"/>. Without one, any HTTP call
    /// throws: the case under test must not reach the gateway.
    /// </summary>
    private WorkspaceSubAgentLoader CreateLoader(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        static HttpResponseMessage UnusedRespond(HttpRequestMessage _) =>
            throw new InvalidOperationException("HTTP not expected in this LoadOneAsync test");

        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(UnusedRespond))
        );

        var registry = new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(respond ?? UnusedRespond)),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );

        return new WorkspaceSubAgentLoader(registry, NullLogger<WorkspaceSubAgentLoader>.Instance);
    }

    private static SandboxSessionRegistry.DiscoveredItem Item(
        string kind,
        string name,
        string path,
        string? content = null,
        string? qualifiedName = null
    ) => new(kind, name, $"{name} description", path, content, qualifiedName);

    /// <summary>
    /// The gateway's files API over <paramref name="files"/> (workspace-relative path to text): a files GET answers
    /// the named file or a 404, and any other GET resolves the session's workspace mount (id 7), recording each
    /// request into <paramref name="requests"/> when given.
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> GatewayFiles(
        Dictionary<string, string> files,
        List<HttpRequestMessage>? requests = null
    ) =>
        request =>
        {
            requests?.Add(request);
            if (!request.RequestUri!.AbsolutePath.Contains("/files/", StringComparison.Ordinal))
            {
                return Json(
                    """{"session_id":"session-load-one-test","volumes":{"workspace":{"container_path":"/workspace","read_only":false,"id":7}}}"""
                );
            }

            return files.TryGetValue(FilePathOf(request), out var text)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"error":"path_not_found","code":404,"error_code":"path_not_found","retryable":false}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
        };

    /// <summary>The workspace-relative path a files GET names.</summary>
    private static string FilePathOf(HttpRequestMessage request) =>
        Uri.UnescapeDataString(request.RequestUri!.Query.TrimStart('?')["path=".Length..]);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task LoadOneAsync_ContainerSession_ReadsTheAgentFileThroughTheGateway()
    {
        // D-SC6-2: on a Docker-backed gateway the session's HostPath is the container path "/workspace", and the
        // webhook names a workspace sub-agent by its container path with no content. The file is not on this host, so
        // the loader must read it through the gateway's files API.
        var requests = new List<HttpRequestMessage>();
        var loader = CreateLoader(
            GatewayFiles(
                new()
                {
                    ["s6/.claude/agents/s6-agent.md"] =
                        "---\nname: s6-agent\ndescription: Runs the sixth scenario demo.\n---\nYou are the s6 agent.\n",
                },
                requests
            )
        );

        var template = await loader.LoadOneAsync(
            CreateSession(hostPathOverride: "/workspace"),
            Item("subagent", "s6-agent", "/workspace/s6/.claude/agents/s6-agent.md"),
            AgentFactory
        );

        template.Should().NotBeNull();
        template!.Name.Should().Be("s6-agent");
        template.Description.Should().Be("Runs the sixth scenario demo.");
        requests
            .Where(r => r.RequestUri!.AbsolutePath.Contains("/files/", StringComparison.Ordinal))
            .Select(FilePathOf)
            .Should()
            .Equal("s6/.claude/agents/s6-agent.md");
    }

    [Fact]
    public async Task LoadOneAsync_HappyPath_ReturnsMappedTemplate()
    {
        // A workspace-relative path reads the same file as its /workspace container path.
        const string relPath = ".claude/agents/echo.md";
        var loader = CreateLoader(GatewayFiles(new() { [relPath] = WellFormedMarkdown }));

        var template = await loader.LoadOneAsync(CreateSession(), Item("subagent", "echo", relPath), AgentFactory);

        template.Should().NotBeNull();
        template!.Name.Should().Be("echo");
        template.Description.Should().Be("Echoes a marker.");
        template.WhenToUse.Should().Be("Echoes a marker.");
        template.SystemPrompt.Should().Contain("echo sub-agent");
        template.MaxTurnsPerRun.Should().Be(WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);
    }

    [Fact]
    public async Task LoadOneAsync_ContentFirst_ParsesInlineBodyWithoutFile()
    {
        // A /marketplaces/... path has no workspace file; the inline content must be parsed directly.
        var loader = CreateLoader();
        var item = Item(
            "subagent",
            "architecture-review",
            "/marketplaces/gb-plugins/agents/architecture-review.md",
            content: WellFormedMarkdown,
            qualifiedName: "code-reviewer:architecture-review"
        );

        var template = await loader.LoadOneAsync(CreateSession(), item, AgentFactory);

        template.Should().NotBeNull();
        template!.SystemPrompt.Should().Contain("echo sub-agent");
    }

    [Fact]
    public async Task LoadOneAsync_NonSubAgentKind_ReturnsNull()
    {
        // Skills (and any other non-subagent kind) must short-circuit BEFORE the file read — the
        // webhook handler relies on this to keep its 200-OK no-op path cheap.
        var loader = CreateLoader();

        var template = await loader.LoadOneAsync(
            CreateSession(),
            Item("skill", "review", ".claude/skills/review.md"),
            AgentFactory
        );

        template.Should().BeNull();
    }

    [Fact]
    public async Task LoadOneAsync_PathTraversal_ReturnsNull()
    {
        // Webhook receives this from an UNTRUSTED gateway payload. The traversal guard MUST sit
        // at LoadOneAsync's boundary, otherwise a malicious "../../etc/passwd" path could be read.
        // The read is refused before it reaches the gateway, which would otherwise serve the file.
        var requests = new List<HttpRequestMessage>();
        var loader = CreateLoader(GatewayFiles(new() { ["../outside.md"] = WellFormedMarkdown }, requests));

        var template = await loader.LoadOneAsync(
            CreateSession(),
            Item("subagent", "escape", "../outside.md"),
            AgentFactory
        );

        template.Should().BeNull();
        requests.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadOneAsync_MissingFile_ReturnsNull()
    {
        var loader = CreateLoader(GatewayFiles([]));

        var template = await loader.LoadOneAsync(
            CreateSession(),
            Item("subagent", "ghost", ".claude/agents/ghost.md"),
            AgentFactory
        );

        template.Should().BeNull();
    }

    [Fact]
    public async Task LoadOneAsync_MalformedMarkdown_ReturnsNull()
    {
        const string relPath = ".claude/agents/bad.md";
        var loader = CreateLoader(GatewayFiles(new() { [relPath] = "no frontmatter at all\n" }));

        var template = await loader.LoadOneAsync(CreateSession(), Item("subagent", "bad", relPath), AgentFactory);

        template.Should().BeNull();
    }

    [Fact]
    public async Task LoadOneAsync_EmptyHostPath_ReturnsNull()
    {
        // A session without a resolved workspace (e.g. a provider that never mounted one) is a
        // valid state — the webhook must not throw or attempt to combine "" + path.
        var loader = CreateLoader();

        var template = await loader.LoadOneAsync(
            CreateSession(hostPathOverride: ""),
            Item("subagent", "echo", ".claude/agents/echo.md"),
            AgentFactory
        );

        template.Should().BeNull();
    }

    [Fact]
    public async Task LoadOneAsync_NullArgs_Throw()
    {
        var loader = CreateLoader();
        var item = Item("subagent", "echo", ".claude/agents/echo.md");

        await Assert.ThrowsAsync<ArgumentNullException>(() => loader.LoadOneAsync(null!, item, AgentFactory));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            loader.LoadOneAsync(CreateSession(), null!, AgentFactory)
        );
        await Assert.ThrowsAsync<ArgumentNullException>(() => loader.LoadOneAsync(CreateSession(), item, null!));
    }

    [Fact]
    public async Task LoadOneAsync_CancellationDuringRead_Propagates()
    {
        // Cancellation MUST propagate (the only exception not absorbed into null) so a webhook
        // tied to an aborted request can bail rather than continue mapping a half-read file.
        const string relPath = ".claude/agents/echo.md";
        var loader = CreateLoader(GatewayFiles(new() { [relPath] = WellFormedMarkdown }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () =>
            loader.LoadOneAsync(CreateSession(), Item("subagent", "echo", relPath), AgentFactory, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(_respond(request));
        }
    }
}
