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
public class WorkspaceSubAgentLoaderLoadOneAsyncTests : IDisposable
{
    private const string SessionId = "session-load-one-test";
    private const string GatewayBaseUrl = "http://localhost:3000";

    private static readonly Mock<IStreamingAgent> AgentStub = new();
    private static readonly Func<IStreamingAgent> AgentFactory = () => AgentStub.Object;

    private readonly string _hostPath;

    public WorkspaceSubAgentLoaderLoadOneAsyncTests()
    {
        _hostPath = Path.Combine(Path.GetTempPath(), "wi77-loadone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_hostPath, ".claude", "agents"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_hostPath))
            {
                Directory.Delete(_hostPath, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static readonly string WellFormedMarkdown =
        """
        ---
        name: echo
        description: Echoes a marker.
        ---
        You are the echo sub-agent. Respond with the marker the user supplies.
        """;

    private SandboxSession CreateSession(string? hostPathOverride = null) => new(
        WorkspaceId: "default",
        SessionId: SessionId,
        WorkspaceRelPath: "default",
        HostPath: hostPathOverride ?? _hostPath);

    private WorkspaceSubAgentLoader CreateLoader()
    {
        // Registry is not exercised by LoadOneAsync — pass a stub HttpClient that throws if used.
        static HttpResponseMessage UnusedRespond(HttpRequestMessage _) =>
            throw new InvalidOperationException("HTTP not expected in LoadOneAsync tests");

        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(UnusedRespond)));

        var registry = new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(UnusedRespond)),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance));

        return new WorkspaceSubAgentLoader(registry, NullLogger<WorkspaceSubAgentLoader>.Instance);
    }

    private static SandboxSessionRegistry.DiscoveredItem Item(
        string kind, string name, string path, string? content = null, string? qualifiedName = null) =>
        new(kind, name, $"{name} description", path, content, qualifiedName);

    [Fact]
    public async Task LoadOneAsync_HappyPath_ReturnsMappedTemplate()
    {
        var relPath = Path.Combine(".claude", "agents", "echo.md");
        await File.WriteAllTextAsync(Path.Combine(_hostPath, relPath), WellFormedMarkdown);
        var loader = CreateLoader();

        var template = await loader.LoadOneAsync(
            CreateSession(),
            Item("subagent", "echo", relPath),
            AgentFactory);

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
            "subagent", "architecture-review", "/marketplaces/gb-plugins/agents/architecture-review.md",
            content: WellFormedMarkdown, qualifiedName: "code-reviewer:architecture-review");

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
            AgentFactory);

        template.Should().BeNull();
    }

    [Fact]
    public async Task LoadOneAsync_PathTraversal_ReturnsNull()
    {
        // Webhook receives this from an UNTRUSTED gateway payload. The traversal guard MUST sit
        // at LoadOneAsync's boundary, otherwise a malicious "../../etc/passwd" path could be read.
        var loader = CreateLoader();

        var template = await loader.LoadOneAsync(
            CreateSession(),
            Item("subagent", "escape", "../outside.md"),
            AgentFactory);

        template.Should().BeNull();
    }

    [Fact]
    public async Task LoadOneAsync_MissingFile_ReturnsNull()
    {
        var loader = CreateLoader();

        var template = await loader.LoadOneAsync(
            CreateSession(),
            Item("subagent", "ghost", ".claude/agents/ghost.md"),
            AgentFactory);

        template.Should().BeNull();
    }

    [Fact]
    public async Task LoadOneAsync_MalformedMarkdown_ReturnsNull()
    {
        var relPath = Path.Combine(".claude", "agents", "bad.md");
        await File.WriteAllTextAsync(Path.Combine(_hostPath, relPath), "no frontmatter at all\n");
        var loader = CreateLoader();

        var template = await loader.LoadOneAsync(
            CreateSession(),
            Item("subagent", "bad", relPath),
            AgentFactory);

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
            AgentFactory);

        template.Should().BeNull();
    }

    [Fact]
    public async Task LoadOneAsync_NullArgs_Throw()
    {
        var loader = CreateLoader();
        var item = Item("subagent", "echo", ".claude/agents/echo.md");

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => loader.LoadOneAsync(null!, item, AgentFactory));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => loader.LoadOneAsync(CreateSession(), null!, AgentFactory));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => loader.LoadOneAsync(CreateSession(), item, null!));
    }

    [Fact]
    public async Task LoadOneAsync_CancellationDuringRead_Propagates()
    {
        // Cancellation MUST propagate (the only exception not absorbed into null) so a webhook
        // tied to an aborted request can bail rather than continue mapping a half-read file.
        var relPath = Path.Combine(".claude", "agents", "echo.md");
        await File.WriteAllTextAsync(Path.Combine(_hostPath, relPath), WellFormedMarkdown);
        var loader = CreateLoader();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => loader.LoadOneAsync(
            CreateSession(),
            Item("subagent", "echo", relPath),
            AgentFactory,
            cts.Token);

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
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_respond(request));
        }
    }
}
