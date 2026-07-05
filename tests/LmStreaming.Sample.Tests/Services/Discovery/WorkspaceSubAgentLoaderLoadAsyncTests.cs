using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using LmStreaming.Sample.Services.Discovery;

namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// End-to-end tests for <see cref="WorkspaceSubAgentLoader.LoadAsync"/>, driving the registry's
/// HTTP path through a stub handler so the loader's full pipeline (gateway → kind-filter →
/// path-resolve → read → parse → map) is exercised against a real on-disk workspace. These
/// pin the contract the unit tests on the static helpers don't cover: that a wrong-kind item
/// is skipped, a gateway failure surfaces as an empty dict (not a throw), a malformed markdown
/// file is skipped without aborting the batch, and two discovered files with the same parsed
/// name collide loader-side under first-wins.
/// </summary>
public class WorkspaceSubAgentLoaderLoadAsyncTests : IDisposable
{
    private const string SessionId = "session-load-test";
    private const string GatewayBaseUrl = "http://localhost:3000";

    private static readonly Mock<IStreamingAgent> AgentStub = new();
    private static readonly Func<IStreamingAgent> AgentFactory = () => AgentStub.Object;

    private readonly string _hostPath;

    public WorkspaceSubAgentLoaderLoadAsyncTests()
    {
        _hostPath = Path.Combine(Path.GetTempPath(), "wi76-loader-" + Guid.NewGuid().ToString("N"));
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

    private SandboxSession CreateSession() => new(
        WorkspaceId: "default",
        SessionId: SessionId,
        WorkspaceRelPath: "default",
        HostPath: _hostPath);

    private (SandboxSessionRegistry Registry, WorkspaceSubAgentLoader Loader) CreateLoader(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var gatewayLifetimeClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)));
        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxGatewayLifetime>.Instance,
            gatewayLifetimeClient);

        var registry = new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(handler),
            new AuthOptions(),
            new AuthSharedSecret(new AuthOptions()));

        var loader = new WorkspaceSubAgentLoader(
            registry,
            NullLogger<WorkspaceSubAgentLoader>.Instance);

        return (registry, loader);
    }

    private static HttpResponseMessage JsonOk(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task LoadAsync_WellFormedSubAgent_ReturnsMappedTemplate()
    {
        var path = Path.Combine(".claude", "agents", "echo.md");
        await File.WriteAllTextAsync(Path.Combine(_hostPath, path), WellFormedMarkdown);
        var (_, loader) = CreateLoader(_ => JsonOk($$"""
            { "discovered": [ { "kind": "subagent", "name": "echo", "description": "Echoes a marker.", "path": "{{path.Replace("\\", "\\\\")}}" } ] }
            """));

        var result = await loader.LoadAsync(CreateSession(), AgentFactory);

        result.Should().ContainKey("echo");
        var template = result["echo"];
        template.Description.Should().Be("Echoes a marker.");
        template.WhenToUse.Should().Be("Echoes a marker.");
        template.SystemPrompt.Should().Contain("echo sub-agent");
        template.MaxTurnsPerRun.Should().Be(WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun);
    }

    [Fact]
    public async Task LoadAsync_NonSubAgentKind_IsSkipped()
    {
        // Kind-filter pin: skills and unknown kinds must not be mapped as sub-agents even when
        // their path resolves to a parseable file. The filter happens before the read.
        var (_, loader) = CreateLoader(_ => JsonOk("""
            { "discovered": [ { "kind": "skill", "name": "review", "description": "x", "path": ".claude/skills/review.md" } ] }
            """));

        var result = await loader.LoadAsync(CreateSession(), AgentFactory);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_GatewayFailure_ReturnsEmptyDictionary()
    {
        // BLOCKER policy: a non-2xx from /discovered must NOT abort agent creation. The loader
        // swallows the throw, logs, and returns an empty dict so the catalog falls back to
        // built-ins only.
        var (_, loader) = CreateLoader(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream went away"),
        });

        var result = await loader.LoadAsync(CreateSession(), AgentFactory);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_FileMissingOnDisk_SkipsButContinuesBatch()
    {
        // Per-file resilience: a discovered item whose path doesn't exist on disk must be
        // logged + skipped without aborting the rest of the batch. Pair an existing file with
        // a missing one and prove only the existing one comes through.
        var goodPath = Path.Combine(".claude", "agents", "echo.md");
        await File.WriteAllTextAsync(Path.Combine(_hostPath, goodPath), WellFormedMarkdown);
        var (_, loader) = CreateLoader(_ => JsonOk($$"""
            {
              "discovered": [
                { "kind": "subagent", "name": "missing", "description": "Missing file.", "path": ".claude/agents/missing.md" },
                { "kind": "subagent", "name": "echo",    "description": "Echoes.",       "path": "{{goodPath.Replace("\\", "\\\\")}}" }
              ]
            }
            """));

        var result = await loader.LoadAsync(CreateSession(), AgentFactory);

        result.Should().HaveCount(1);
        result.Should().ContainKey("echo");
    }

    [Fact]
    public async Task LoadAsync_MalformedMarkdown_IsSkipped()
    {
        // Parser returns null on malformed input → loader logs + skips that file, keeps others.
        var goodPath = Path.Combine(".claude", "agents", "echo.md");
        var badPath = Path.Combine(".claude", "agents", "bad.md");
        await File.WriteAllTextAsync(Path.Combine(_hostPath, goodPath), WellFormedMarkdown);
        await File.WriteAllTextAsync(Path.Combine(_hostPath, badPath), "no frontmatter at all\n");
        var (_, loader) = CreateLoader(_ => JsonOk($$"""
            {
              "discovered": [
                { "kind": "subagent", "name": "bad",  "description": "x", "path": "{{badPath.Replace("\\", "\\\\")}}" },
                { "kind": "subagent", "name": "echo", "description": "x", "path": "{{goodPath.Replace("\\", "\\\\")}}" }
              ]
            }
            """));

        var result = await loader.LoadAsync(CreateSession(), AgentFactory);

        result.Should().ContainKey("echo");
        result.Should().NotContainKey("bad");
    }

    [Fact]
    public async Task LoadAsync_PathTraversalAttempt_IsSkipped()
    {
        // Path-injection guard at the loader boundary: a "../" item must never be read.
        var (_, loader) = CreateLoader(_ => JsonOk("""
            { "discovered": [ { "kind": "subagent", "name": "escape", "description": "x", "path": "../outside.md" } ] }
            """));

        var result = await loader.LoadAsync(CreateSession(), AgentFactory);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_TwoDiscoveriesSameParsedName_FirstWins()
    {
        // Loader-level collision (distinct from the built-in collision in MergeBuiltInWins):
        // two discovered markdown files whose frontmatter declares the same `name` must
        // collapse to the first occurrence so the dict insert can't throw.
        var firstPath = Path.Combine(".claude", "agents", "echo-a.md");
        var secondPath = Path.Combine(".claude", "agents", "echo-b.md");
        await File.WriteAllTextAsync(
            Path.Combine(_hostPath, firstPath),
            """
            ---
            name: echo
            description: First.
            ---
            FIRST body.
            """);
        await File.WriteAllTextAsync(
            Path.Combine(_hostPath, secondPath),
            """
            ---
            name: echo
            description: Second.
            ---
            SECOND body.
            """);
        var (_, loader) = CreateLoader(_ => JsonOk($$"""
            {
              "discovered": [
                { "kind": "subagent", "name": "echo-a", "description": "x", "path": "{{firstPath.Replace("\\", "\\\\")}}" },
                { "kind": "subagent", "name": "echo-b", "description": "x", "path": "{{secondPath.Replace("\\", "\\\\")}}" }
              ]
            }
            """));

        var result = await loader.LoadAsync(CreateSession(), AgentFactory);

        result.Should().HaveCount(1);
        result["echo"].SystemPrompt.Should().Contain("FIRST");
    }

    [Fact]
    public async Task LoadAsync_EmptyHostPath_ReturnsEmpty()
    {
        // A session without a resolved host workspace (e.g. gateway never mounted one) is a
        // valid mode for non-workspace providers — the loader must short-circuit, not throw.
        var session = new SandboxSession("default", SessionId, "default", HostPath: "");
        var (_, loader) = CreateLoader(_ => JsonOk("""
            { "discovered": [ { "kind": "subagent", "name": "echo", "description": "x", "path": "echo.md" } ] }
            """));

        var result = await loader.LoadAsync(session, AgentFactory);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_NullArguments_Throw()
    {
        var (_, loader) = CreateLoader(_ => JsonOk("""{ "discovered": [] }"""));

        await Assert.ThrowsAsync<ArgumentNullException>(() => loader.LoadAsync(null!, AgentFactory));
        await Assert.ThrowsAsync<ArgumentNullException>(() => loader.LoadAsync(CreateSession(), null!));
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
