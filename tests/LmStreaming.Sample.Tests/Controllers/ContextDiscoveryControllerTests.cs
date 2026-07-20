using System.Collections.Immutable;
using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Tests for <see cref="ContextDiscoveryController.NotifyAsync"/>: 401 on missing/wrong shared
/// secret, 400 on malformed payload, 200 on the happy path (including best-effort no-ops when
/// the session/binding aren't known yet — discovery is an enrichment, not a precondition).
/// </summary>
public class ContextDiscoveryControllerTests
{
    private const string Secret = "test-shared-secret-1234";
    private const string GatewayBaseUrl = "http://localhost:3000";

    // Fixed session ids used by tests that don't care about session-specific secret isolation —
    // all pre-seeded with Secret in the default store CreateController builds when the caller
    // doesn't supply its own SessionSecretStore.
    private static readonly string[] WellKnownSessionIds =
    [
        "session-x",
        "session-dispatch",
        "session-diag",
        "session-not-in-registry",
        "session-skill-only",
        "session-noauth",
        "session-wrongsecret",
        "session-validate",
    ];

    private static ContextDiscoveryController CreateController(
        string? authorizationHeader,
        SandboxSessionRegistry? registry = null,
        WorkspaceSubAgentLoader? loader = null,
        ContextDiscoveryInjector? injector = null,
        ContextDiscoveryDiagnostics? diagnostics = null,
        SessionSecretStore? sessionSecretStore = null)
    {
        sessionSecretStore ??= CreateSeededSessionSecretStore();

        registry ??= CreateEmptyRegistry();
        loader ??= new WorkspaceSubAgentLoader(registry, NullLogger<WorkspaceSubAgentLoader>.Instance);
        injector ??= CreateNoopInjector(registry);
        diagnostics ??= new ContextDiscoveryDiagnostics();

        var controller = new ContextDiscoveryController(
            sessionSecretStore,
            registry,
            loader,
            injector,
            diagnostics,
            NullLogger<ContextDiscoveryController>.Instance);

        var httpContext = new DefaultHttpContext();
        if (authorizationHeader is not null)
        {
            httpContext.Request.Headers.Authorization = authorizationHeader;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static SessionSecretStore CreateSeededSessionSecretStore()
    {
        var store = new SessionSecretStore(
            Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
            NullLogger<SessionSecretStore>.Instance);

        foreach (var sessionId in WellKnownSessionIds)
        {
            store.SaveAsync(sessionId, Secret).GetAwaiter().GetResult();
        }

        return store;
    }

    private static ContextDiscoveryInjector CreateNoopInjector(SandboxSessionRegistry registry)
    {
        // The pool is real but unwired — TryGet returns false for every threadId since the test
        // never calls GetOrCreateAgent. That keeps the injector's per-thread send loop a no-op
        // while still letting it exercise the validation/dedup paths against the real registry.
        var pool = new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);

        return new ContextDiscoveryInjector(
            registry,
            pool,
            new ContextDiscoveryFormatter(),
            new ContextDiscoveryOptions(),
            new ContextDiscoveryDiagnostics(),
            NullLogger<ContextDiscoveryInjector>.Instance);
    }

    private static SandboxSessionRegistry CreateEmptyRegistry()
    {
        // No HTTP traffic occurs in these tests — the controller's session/binding lookups go
        // through the in-memory dictionaries only — so a stub handler that throws on use is the
        // safest contract.
        static HttpResponseMessage UnusedRespond(HttpRequestMessage _) =>
            throw new InvalidOperationException("HTTP not expected in ContextDiscoveryControllerTests");

        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(UnusedRespond)));

        return new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(UnusedRespond)),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance));
    }

    // The gateway delivers a BATCHED `context_discovery` envelope (a `discoveries` array with the
    // session id at the envelope level). These helpers wrap a single item in that envelope so each
    // test reads as "one discovered item for a session".
    private static ContextDiscoveryEnvelope Envelope(string? sessionId, params ContextDiscoveryItem[] items)
        => new() { SessionId = sessionId, Discoveries = [.. items] };

    [Fact]
    public async Task NotifyAsync_NoAuthorizationHeader_ReturnsUnauthorized()
    {
        var controller = CreateController(authorizationHeader: null);
        var body = Envelope("session-noauth", new ContextDiscoveryItem { Kind = "subagent", Name = "echo" });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task NotifyAsync_WrongSecret_ReturnsUnauthorized()
    {
        var controller = CreateController(authorizationHeader: "wrong-secret");
        var body = Envelope("session-wrongsecret", new ContextDiscoveryItem { Kind = "subagent", Name = "echo" });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_NullBody_ReturnsBadRequest()
    {
        var controller = CreateController(authorizationHeader: Secret);

        var result = await controller.NotifyAsync(null, CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task NotifyAsync_NonNullBody_MissingSessionId_ReturnsUnauthorized()
    {
        // Distinct from the genuinely-null-body case above (BadRequest): a well-formed envelope
        // with no session id can never be resolved to a per-session secret, so it must 401 rather
        // than reach MatchesAsync at all.
        var controller = CreateController(authorizationHeader: Secret);
        var body = Envelope(sessionId: null, new ContextDiscoveryItem { Kind = "subagent", Name = "echo" });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task NotifyAsync_SecretValidForAnotherSession_ReturnsUnauthorized()
    {
        // The actual cross-session-isolation guarantee: session A's real secret must not
        // authenticate a call claiming to be session B, even though both sessions are known.
        var sessionSecretStore = new SessionSecretStore(
            Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
            NullLogger<SessionSecretStore>.Instance);
        await sessionSecretStore.SaveAsync("session-a", "secret-for-a");
        await sessionSecretStore.SaveAsync("session-b", "secret-for-b");

        var controller = CreateController(authorizationHeader: "secret-for-a", sessionSecretStore: sessionSecretStore);
        var body = Envelope("session-b", new ContextDiscoveryItem { Kind = "subagent", Name = "echo" });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_MissingKind_ReturnsBadRequest()
    {
        var controller = CreateController(authorizationHeader: Secret);
        var body = Envelope("session-validate", new ContextDiscoveryItem { Name = "echo" });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_SubAgentMissingName_ReturnsBadRequest()
    {
        // Sub-agent activations are keyed by name (the value the model picks via the Agent tool's
        // subagent_type enum). Without it we can't register or look up the template, so reject.
        var controller = CreateController(authorizationHeader: Secret);
        var body = Envelope("session-validate", new ContextDiscoveryItem { Kind = "subagent" });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_ContextFileMissingPath_ReturnsBadRequest()
    {
        // context_file deliveries are keyed by path (the dedup key + the pill label) — a missing
        // path can't be deduped against retries and can't be displayed to the user.
        var controller = CreateController(authorizationHeader: Secret);
        var body = Envelope("session-validate", new ContextDiscoveryItem { Kind = "context_file", Content = "body" });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_ContextFileMissingContent_ReturnsBadRequest()
    {
        // Null content means the gateway has nothing for the model to read; the injector would
        // drop it anyway, but rejecting at the boundary keeps the contract crisp.
        var controller = CreateController(authorizationHeader: Secret);
        var body = Envelope("session-validate", new ContextDiscoveryItem { Kind = "context_file", Path = "CLAUDE.md" });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_ContextFile_EmptyContent_IsAccepted()
    {
        // Empty (but non-null) content passes validation — the gateway may legitimately deliver an
        // empty file. The injector treats it as a drop downstream so nothing reaches the model,
        // but the contract at the boundary is "non-null is acceptable".
        var controller = CreateController(authorizationHeader: Secret);
        var body = Envelope("session-x", new ContextDiscoveryItem
        {
            Kind = "context_file",
            Path = "CLAUDE.md",
            Content = string.Empty,
        });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_ContextFile_DispatchesToInjector()
    {
        // Verifies the controller actually invokes the injector for context_file deliveries by
        // observing the registry's dedup side effect: after the first successful dispatch,
        // TryMarkDiscoverySeen for the same (sessionId, kind, path) must return false.
        var registry = CreateEmptyRegistry();
        var controller = CreateController(authorizationHeader: Secret, registry: registry);

        var body = Envelope("session-dispatch", new ContextDiscoveryItem
        {
            Kind = "context_file",
            Path = "CLAUDE.md",
            Content = "body",
        });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
        registry
            .TryMarkDiscoverySeen("session-dispatch", SandboxSessionRegistry.SessionDiscoveryTarget, "context_file", "CLAUDE.md")
            .Should().BeFalse("the injector should have already marked this entry as seen during dispatch");
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_ContextFile_RecordsArrivalInDiagnostics()
    {
        // The diagnostics endpoint reads these counts to prove webhooks are actually arriving.
        var diagnostics = new ContextDiscoveryDiagnostics();
        var controller = CreateController(authorizationHeader: Secret, diagnostics: diagnostics);

        var result = await controller.NotifyAsync(
            Envelope("session-diag", new ContextDiscoveryItem
            {
                Kind = "context_file",
                Path = "CLAUDE.md",
                Content = "body",
            }),
            CancellationToken.None);

        result.Should().BeOfType<OkResult>();
        var snapshot = diagnostics.Snapshot();
        snapshot.Should().ContainKey("session-diag");
        snapshot["session-diag"].Count.Should().Be(1);
        snapshot["session-diag"].LastPath.Should().Be("CLAUDE.md");
    }

    [Fact]
    public async Task NotifyAsync_Unauthorized_DoesNotRecordArrival()
    {
        // An unauthenticated call must never be counted — otherwise the diagnostic would mask a
        // gateway that can't authenticate as if discoveries were flowing.
        var diagnostics = new ContextDiscoveryDiagnostics();
        var controller = CreateController(authorizationHeader: "wrong-secret", diagnostics: diagnostics);

        _ = await controller.NotifyAsync(
            Envelope("session-diag", new ContextDiscoveryItem
            {
                Kind = "context_file",
                Path = "CLAUDE.md",
                Content = "body",
            }),
            CancellationToken.None);

        diagnostics.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_CorrectSecret_WellFormedPayload_ReturnsOk()
    {
        var controller = CreateController(authorizationHeader: Secret);
        var body = Envelope("session-validate", new ContextDiscoveryItem
        {
            Kind = "subagent",
            Name = "echo",
            Description = "Echoes a marker.",
            Path = ".claude/agents/echo.md",
        });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task NotifyAsync_NonSubAgentKind_NoSession_ReturnsOk()
    {
        // Non-subagent kinds are still logged as discoveries but never activate — the controller
        // must NOT try to resolve a sub-agent binding for them. Verifies that path stays a no-op
        // even for a session with no sub-agent bindings registered.
        var controller = CreateController(authorizationHeader: Secret);
        var body = Envelope("session-skill-only", new ContextDiscoveryItem
        {
            Kind = "skill",
            Name = "review-skill",
            Path = ".claude/skills/review.md",
        });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task NotifyAsync_SubAgentPayload_UnknownSession_ReturnsOkWithoutActivation()
    {
        // Discovery firing before the agent path has initialised the session must NOT fail —
        // the gateway shouldn't see a 5xx for an enrichment event. Best-effort no-op + 200 is
        // the contract.
        var registry = CreateEmptyRegistry();
        var controller = CreateController(
            authorizationHeader: Secret,
            registry: registry);

        var body = Envelope("session-not-in-registry", new ContextDiscoveryItem
        {
            Kind = "subagent",
            Name = "ghost",
            Path = ".claude/agents/ghost.md",
        });

        var result = await controller.NotifyAsync(body, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task NotifyAsync_SubAgentDiscovery_RegistersEachConversationsOwnAgentFactory()
    {
        // Provider-bleed regression: a sub-agent discovered while TWO conversations are live on the
        // shared session is loaded once, but each conversation must receive a template wired to ITS
        // OWN AgentFactory — not the first conversation's. Otherwise conversation B's discovered
        // sub-agent would spawn using conversation A's provider.
        var hostPath = Path.Combine(Path.GetTempPath(), "wi-bleed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(hostPath, ".claude", "agents"));
        await File.WriteAllTextAsync(
            Path.Combine(hostPath, ".claude", "agents", "echo.md"),
            "---\nname: echo\ndescription: Echoes a marker.\n---\nYou are the echo sub-agent.");

        try
        {
            const string gatewaySessionId = "sess-bleed";

            HttpResponseMessage Respond(HttpRequestMessage req)
            {
                if (req.Method == HttpMethod.Post
                    && req.RequestUri!.AbsolutePath.Contains("/sandboxes", StringComparison.Ordinal))
                {
                    var containerPath = System.Text.Json.JsonSerializer.Serialize(hostPath);
                    var json =
                        "{\"session_id\":\"" + gatewaySessionId + "\",\"volumes\":{\"workspace\":{\"container_path\":"
                        + containerPath + ",\"read_only\":false}}}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                // Health probe (and anything else) → healthy.
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var gateway = new SandboxGatewayLifetime(
                new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, AutoSpawn = false },
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(new StubHandler(Respond)));

            var sessionSecretStore = new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance);

            await using var registry = new SandboxSessionRegistry(
                gateway,
                new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, AutoSpawn = false },
                NullLogger<SandboxSessionRegistry>.Instance,
                new HttpClient(new StubHandler(Respond)),
                new AuthOptions(),
                sessionSecretStore);

            // Create the shared session so the webhook can resolve it by id. The registry
            // auto-generates its own random per-session secret on creation — overwrite it with the
            // known test Secret so the later controller call (built with authorizationHeader: Secret)
            // can authenticate against this exact session.
            var session = await registry.GetOrCreateSessionAsync("default");
            session.SessionId.Should().Be(gatewaySessionId);
            await sessionSecretStore.SaveAsync(gatewaySessionId, Secret);

            var agentA = new Mock<IStreamingAgent>().Object;
            var agentB = new Mock<IStreamingAgent>().Object;
            var characteristicsAgentA = new Mock<IStreamingAgent>().Object;
            var characteristicsAgentB = new Mock<IStreamingAgent>().Object;
            SubAgentCharacteristics? receivedCharacteristicsB = null;
            Func<SubAgentCharacteristics, SubAgentProviderAgent> characteristicsFactoryA =
                _ => new SubAgentProviderAgent(
                    characteristicsAgentA,
                    ImmutableDictionary<string, object?>.Empty);
            Func<SubAgentCharacteristics, SubAgentProviderAgent> characteristicsFactoryB =
                characteristics =>
                {
                    receivedCharacteristicsB = characteristics;
                    return new SubAgentProviderAgent(
                        characteristicsAgentB,
                        ImmutableDictionary<string, object?>.Empty);
                };
            var emptySeed = new Dictionary<string, SubAgentTemplate>();
            var bindingA = registry.AddOrUpdateSubAgentBinding(
                gatewaySessionId,
                "conv-A",
                emptySeed,
                () => agentA,
                characteristicsFactoryA);
            var bindingB = registry.AddOrUpdateSubAgentBinding(
                gatewaySessionId,
                "conv-B",
                emptySeed,
                () => agentB,
                characteristicsFactoryB);

            var loader = new WorkspaceSubAgentLoader(registry, NullLogger<WorkspaceSubAgentLoader>.Instance);
            var controller = CreateController(
                authorizationHeader: Secret,
                registry: registry,
                loader: loader,
                sessionSecretStore: sessionSecretStore);

            var result = await controller.NotifyAsync(
                Envelope(gatewaySessionId, new ContextDiscoveryItem
                {
                    Kind = "subagent",
                    Name = "echo",
                    Path = ".claude/agents/echo.md",
                }),
                CancellationToken.None);

            result.Should().BeOfType<OkResult>();

            bindingA.Source.Templates.Should().ContainKey("echo");
            bindingB.Source.Templates.Should().ContainKey("echo");
            bindingA.Source.Templates["echo"].AgentFactory().Should().BeSameAs(agentA);
            bindingB.Source.Templates["echo"].AgentFactory().Should().BeSameAs(
                agentB,
                "conversation B's discovered sub-agent must spawn with B's provider, not the first conversation's");
            bindingA.Source.Templates["echo"].CharacteristicsAgentFactory.Should().NotBeNull();
            bindingB.Source.Templates["echo"].CharacteristicsAgentFactory.Should().NotBeNull();

            var characteristics = new SubAgentCharacteristics("conversation-b-model", ReasoningEffort.High);
            var reboundProvider = bindingB.Source.Templates["echo"]
                .CharacteristicsAgentFactory!(characteristics);

            receivedCharacteristicsB.Should().BeSameAs(characteristics);
            reboundProvider.Agent.Should().BeSameAs(
                agentB,
                "inherited webhook spawns must use the conversation's fresh legacy route");
            var explicitProvider = bindingB.Source.Templates["echo"]
                .CharacteristicsAgentFactory!(
                    characteristics with { IsModelExplicitlySelected = true });
            explicitProvider.Agent.Should().BeSameAs(
                characteristicsAgentB,
                "explicit webhook spawns must use the conversation's characteristics route");
        }
        finally
        {
            try
            {
                if (Directory.Exists(hostPath))
                {
                    Directory.Delete(hostPath, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
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
