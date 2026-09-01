using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Pins the claim no other test makes: the system prompt the host COMPOSES is the system prompt the
/// model RECEIVES.
/// <para>
/// Every other test of this machinery stops one hop short. <c>ConversationsControllerTests</c> proves
/// provision writes the appendix into thread metadata and that <c>SystemPromptAugmenter.ComposeAsync</c>
/// returns it in the right position — but it asserts on a returned STRING, never on an outbound request.
/// <c>LmStreamingS2SClientTests</c> proves the daemon SENDS the appendix, which is delivery to the host,
/// not application to the model. Between the composed string and the wire sits the agent-factory lambda
/// in <c>Program.cs</c> (~1100 lines, no unit-test seam), and that gap is exactly where this field spent
/// its entire life inert: stored, and read by nothing.
/// </para>
/// <para>
/// The gap was invisible rather than merely uncovered. The real factory IS executed by this E2E harness —
/// <c>E2EWebAppFactory</c> boots the production DI graph and replaces only <c>ITestAgentBuilder</c> — so
/// the call site ran in every scenario here. But <c>AppendCallerInstructions</c> is a no-op on a
/// null/blank appendix, and no test had ever set one, so deleting the call produced byte-identical
/// behavior everywhere. A call site that no test can distinguish from its own deletion is not covered by
/// the tests that happen to execute it.
/// </para>
/// <para>
/// So this test supplies the one input that makes the call site observable, and reads the prompt back off
/// the outbound LLM request rather than from any host-side return value.
/// </para>
/// </summary>
public sealed class SystemPromptCompositionTests
{
    /// <summary>
    /// Deliberately unlike anything in a mode prompt, a workspace suffix or a discovered CLAUDE.md block,
    /// so a match cannot come from any source but the provisioned appendix.
    /// </summary>
    private const string AppendixMarker =
        "CALLER-APPENDIX-MARKER: obey the caller's review methodology and output contract.";

    /// <summary>
    /// Substring of the sample's default mode prompt. Its presence proves the appendix is ADDITIVE — the
    /// host-built prompt survives — which is the property that separates this fix from one that replaces
    /// the mode prompt wholesale.
    /// </summary>
    private const string ModePromptMarker = "helpful assistant";

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Provisioned_appendix_reaches_the_model_last_in_the_composed_system_prompt(string providerMode)
    {
        // Captured from inside the role predicate, which the scripted handler invokes with the parsed
        // OUTBOUND request. This is the whole point of the test: the assertion subject is the prompt as
        // the provider received it, not a value any host-side code handed back to us.
        string? promptTheModelReceived = null;

        var responder = ScriptedSseResponder
            .New()
            .ForRole(
                "parent",
                ctx =>
                {
                    promptTheModelReceived ??= ctx.SystemPrompt;
                    return true;
                }
            )
            .Turn(t => t.Text("ack"))
            .Build();

        var handler = providerMode == "test-anthropic" ? responder.AsAnthropicHandler() : responder.AsOpenAiHandler();

        var builder = new ScriptedBuilder(handler);
        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"appendix-{providerMode}-{Guid.NewGuid():N}";

        // Seed the appendix the way provision does, under the same key production reads. The provision
        // ENDPOINT's write is already pinned by
        // ConversationsControllerTests.Provision_PersistsTheCallerInstructions_AndTheAgentBuildReadsThemBack;
        // going through HTTP here would additionally require a resolvable workspace, mode and provider,
        // which would gate this test on environment rather than on the behavior under test. Both sides
        // reference SystemPromptAugmenter.AppendixPropertyKey, so there is no literal to drift apart.
        var store = factory.Services.GetRequiredService<IConversationStore>();
        await store.UpdateMetadataAsync(
            threadId,
            existing =>
            {
                var properties =
                    existing?.Properties?.ToBuilder() ?? ImmutableDictionary.CreateBuilder<string, object>();
                properties[SystemPromptAugmenter.AppendixPropertyKey] = AppendixMarker;

                return new ThreadMetadata
                {
                    ThreadId = threadId,
                    CurrentRunId = existing?.CurrentRunId,
                    LatestRunId = existing?.LatestRunId,
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SessionMappings = existing?.SessionMappings,
                    Properties = properties.ToImmutable(),
                };
            }
        );

        // Guard the fixture before trusting the wire assertion below. If the seed is not readable through
        // the exact reader production uses, a failure downstream would be this test's own setup rather
        // than the host dropping the appendix — two very different findings that look identical.
        var seeded = await SystemPromptAugmenter.ReadAppendixAsync(store, threadId);
        seeded.Should().Be(AppendixMarker, "the seed must be visible to production's own reader");

        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);
        await client.SendUserMessageAsync("say hello");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(15));

        // Guard the instrument before trusting it: if the turn never reached the provider there would be
        // no captured prompt, and every assertion below would be vacuous rather than failing.
        frames.ConcatText().Should().Contain("ack");
        promptTheModelReceived
            .Should()
            .NotBeNull("the scripted provider must have received a request to capture a prompt from");

        var prompt = promptTheModelReceived!;

        // 1. The claim under test. Deleting the ComposeAsync call in Program.cs fails exactly here.
        prompt
            .Should()
            .Contain(
                AppendixMarker,
                "the caller's instructions must reach the model, not merely the thread's metadata"
            );

        // 2. Additive, not a replacement — the host-built prompt is still there.
        prompt.Should().Contain(ModePromptMarker);

        // 3. PrependCurrentDate reached the model too. Same class of call site, same failure mode: it is
        //    applied while the mode is resolved and nothing else observes it end-to-end.
        prompt.Should().Contain("The current date is");

        // 4. Ordering, end-to-end. The appendix is last because recency is load-bearing — the caller is
        //    adding a task on top of a workspace agent. ConversationsControllerTests pins this on the
        //    composed string; this pins it on what actually went over the wire.
        prompt.TrimEnd().Should().EndWith(AppendixMarker);
        prompt
            .IndexOf(AppendixMarker, StringComparison.Ordinal)
            .Should()
            .BeGreaterThan(
                prompt.IndexOf(ModePromptMarker, StringComparison.Ordinal),
                "the appendix must follow every host-built section, not precede them"
            );
    }

    /// <summary>
    /// The #628 composition pin, full fidelity: a review conversation provisioned in the
    /// <c>code-review-daemon</c> mode reaches the model as
    /// <c>date + mode prompt + workspace enrichment + daemon appendix</c>, in that order. Extends
    /// the pin above to the daemon's actual mode, whose sandbox capability adds the workspace
    /// suffix between the mode prompt and the appendix — a section the default-mode test cannot
    /// observe. Gated on a real sandbox gateway (same prerequisite as
    /// <see cref="SandboxWorkspaceGatewayE2ETests"/>), because the workspace enrichment only
    /// exists when a session is actually established; without a gateway the test skips.
    /// </summary>
    [SkippableFact]
    public async Task CodeReviewDaemonMode_ComposesDateThenModeThenWorkspaceThenAppendix_InOrder()
    {
        var prereq = SandboxGatewayPrerequisites.Detect();
        Skip.IfNot(prereq.Available, prereq.SkipReason);
        using var config = prereq.CreateConfigScope();

        string? promptTheModelReceived = null;
        var responder = ScriptedSseResponder
            .New()
            .ForRole(
                "review-parent",
                ctx =>
                {
                    promptTheModelReceived ??= ctx.SystemPrompt;
                    return true;
                }
            )
            .Turn(t => t.Text("ack"))
            .Build();

        // test-anthropic: the provider mode the Workspace-Agent-style sandbox guard accepts, same
        // as SandboxWorkspaceGatewayE2ETests.
        using var factory = new E2EWebAppFactory("test-anthropic", new ScriptedBuilder(responder.AsAnthropicHandler()));

        var threadId = $"crd-mode-{Guid.NewGuid():N}";

        // Seed the daemon's appendix the way provision does (same key production reads).
        var store = factory.Services.GetRequiredService<IConversationStore>();
        await store.UpdateMetadataAsync(
            threadId,
            existing =>
            {
                var properties =
                    existing?.Properties?.ToBuilder() ?? ImmutableDictionary.CreateBuilder<string, object>();
                properties[SystemPromptAugmenter.AppendixPropertyKey] = AppendixMarker;

                return new ThreadMetadata
                {
                    ThreadId = threadId,
                    CurrentRunId = existing?.CurrentRunId,
                    LatestRunId = existing?.LatestRunId,
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SessionMappings = existing?.SessionMappings,
                    Properties = properties.ToImmutable(),
                };
            }
        );

        var socket = await factory.ConnectWebSocketAsync(
            threadId,
            LmStreaming.Sample.Persistence.SystemChatModes.CodeReviewDaemonModeId
        );
        await using var client = new WebSocketTestClient(socket);
        await client.SendUserMessageAsync("begin the review");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(120));

        frames.ConcatText().Should().Contain("ack");
        promptTheModelReceived
            .Should()
            .NotBeNull("the scripted provider must have received a request to capture a prompt from");

        var prompt = promptTheModelReceived!;

        // The four sections, each present...
        prompt.Should().StartWith("The current date is", "the date line is prepended at the mode entry point");
        prompt.Should().Contain("Revobot", "the code-review-daemon mode prompt must reach the model");
        prompt
            .Should()
            .Contain("Your workspace directory is:", "the sandbox workspace enrichment must reach the model");
        prompt
            .Should()
            .Contain(
                "/workspace/store/KnowledgeBase/",
                "issue #648 ruling: the fixed exact-path KB navigation must reach the primary"
            );
        prompt
            .Should()
            .Contain(
                "do NOT start from a KnowledgeBase/_toc.md file",
                "the no-search/no-_toc.md-start rule must reach the primary"
            );
        prompt
            .Should()
            .NotContain(
                "Start with KnowledgeBase/_toc.md",
                "the superseded relative-navigation wording must not return"
            );
        prompt.TrimEnd().Should().EndWith(AppendixMarker, "the daemon's appendix is composed last");

        // ...and in exactly the pinned order: date + mode prompt + workspace enrichment + appendix.
        var modeIndex = prompt.IndexOf("Revobot", StringComparison.Ordinal);
        var workspaceIndex = prompt.IndexOf("Your workspace directory is:", StringComparison.Ordinal);
        var appendixIndex = prompt.IndexOf(AppendixMarker, StringComparison.Ordinal);
        modeIndex.Should().BePositive("the date line precedes the mode prompt");
        workspaceIndex.Should().BeGreaterThan(modeIndex, "the workspace enrichment follows the mode prompt");
        appendixIndex.Should().BeGreaterThan(workspaceIndex, "the appendix follows the workspace enrichment");
    }

    /// <summary>
    /// F-004: <c>effectiveMode = ApplyWorkspaceSuffix(mode, wsSuffix)</c> in <c>Program.cs</c>'s agent
    /// factory was reachable in this suite only through <see cref="CodeReviewDaemonMode_ComposesDateThenModeThenWorkspaceThenAppendix_InOrder"/>,
    /// a <see cref="SkippableFactAttribute"/> gated on a live sandbox gateway — green-by-skip in
    /// ordinary CI, so deleting the call site changed nothing any non-skipped test could see.
    /// </summary>
    /// <remarks>
    /// This test reaches the same real, unmodified agent-factory branch without a live gateway: it
    /// stubs the sandbox gateway's HTTP surface (health probe, session create, session-liveness probe)
    /// and swaps in an isolated <see cref="LmStreaming.Sample.Persistence.FileChatModeStore"/> so it can
    /// call the production <c>CopyModeAsync</c> seam to obtain a COPY of the daemon mode under a fresh
    /// id. <c>caps.NeedsSandbox</c> is resolved from <c>EnabledCapabilityTools</c>, not from the mode's
    /// id (see <c>Program.cs</c>), so the copy takes the sandbox branch exactly like the original —
    /// proving the call site is reachable for any sandbox-capable mode, not merely a hard-coded id.
    /// </remarks>
    [Fact]
    public async Task SandboxCapableModeCopy_StillReceivesTheWorkspaceSuffix_ThroughTheRealAgentFactory()
    {
        // Isolated user-mode store so CopyModeAsync's write does not touch the shared production
        // chat-modes.json path (see ModeCapabilitiesCloneTests for the same precedent).
        var modeStoreDir = Path.Combine(Path.GetTempPath(), "lmstreaming-f004-modes-" + Guid.NewGuid().ToString("N"));
        var secretStoreDir = Path.Combine(
            Path.GetTempPath(),
            "lmstreaming-f004-secrets-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            string? promptTheModelReceived = null;
            var responder = ScriptedSseResponder
                .New()
                .ForRole(
                    "sandbox-mode-copy",
                    ctx =>
                    {
                        promptTheModelReceived ??= ctx.SystemPrompt;
                        return true;
                    }
                )
                .Turn(t => t.Text("ack"))
                .Build();

            var chatModeStore = new LmStreaming.Sample.Persistence.FileChatModeStore(modeStoreDir);
            var copiedMode = await chatModeStore.CopyModeAsync(
                LmStreaming.Sample.Persistence.SystemChatModes.CodeReviewDaemonModeId,
                "F-004 sandbox-capable copy"
            );
            copiedMode.Id.Should().NotBe(LmStreaming.Sample.Persistence.SystemChatModes.CodeReviewDaemonModeId);

            var gatewayOptions = new SandboxGatewayOptions
            {
                BaseUrl = "http://127.0.0.1:3000",
                WorkspaceBasePath = null,
                Workspace = "default-leaf",
            };

            var stubHandler = new StubSandboxGatewayHandler();
            var gatewayLifetime = new SandboxGatewayLifetime(
                gatewayOptions,
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(stubHandler)
            );
            var sandboxRegistry = new SandboxSessionRegistry(
                gatewayLifetime,
                gatewayOptions,
                NullLogger<SandboxSessionRegistry>.Instance,
                new HttpClient(stubHandler),
                new AuthOptions(),
                new SessionSecretStore(secretStoreDir, NullLogger<SessionSecretStore>.Instance)
            );

            // The real agent factory validates the resolved workspace's marketplace selection via
            // WorkspaceCatalogCompatibilityService BEFORE it establishes the sandbox session (see
            // Program.cs, guarded by `if (workspace is not null)`). That service depends on
            // IMarketplaceCatalogClient, which — unlike the two overrides above — was NOT swapped for
            // a test double: it stayed the real MarketplaceCatalogClient, wired to a live HttpClient
            // pointed at SandboxGatewayOptions.BaseUrl. On a clean CI host nothing answers that
            // address, so the real client fails, the compatibility service reports the catalog
            // Unavailable, and Program.cs fails the request closed — before it ever reaches the LLM.
            // The default workspace's Marketplaces list is empty (FileWorkspaceStore), so any
            // catalog that answers WITHOUT throwing is already "compatible"; this stub only needs to
            // answer successfully. CallCount below proves the DI override is actually wired into the
            // production graph and consulted by it — not, on its own, that
            // WorkspaceCatalogCompatibilityService.ValidateForSessionAsync specifically is the caller:
            // the copied daemon mode enables subagents, and MarketplaceSubAgentLoader can reach the
            // same IMarketplaceCatalogClient, so a nonzero count is consistent with either call site
            // (or both) having run.
            var catalogClient = new StubMarketplaceCatalogClient();

            var builder = new ScriptedBuilder(responder.AsAnthropicHandler());
            using var factory = new E2EWebAppFactory(
                "test-anthropic",
                builder,
                configureServices: services =>
                {
                    services.RemoveAll<LmStreaming.Sample.Persistence.IChatModeStore>();
                    services.AddSingleton<LmStreaming.Sample.Persistence.IChatModeStore>(chatModeStore);
                    services.RemoveAll<SandboxGatewayLifetime>();
                    services.AddSingleton(gatewayLifetime);
                    services.RemoveAll<SandboxSessionRegistry>();
                    services.AddSingleton(sandboxRegistry);
                    services.RemoveAll<IMarketplaceCatalogClient>();
                    services.AddSingleton<IMarketplaceCatalogClient>(catalogClient);
                }
            );

            var threadId = $"f004-{Guid.NewGuid():N}";
            var socket = await factory.ConnectWebSocketAsync(threadId, copiedMode.Id);
            await using var client = new WebSocketTestClient(socket);
            await client.SendUserMessageAsync("begin the review");
            using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

            // With every external dependency (chat-mode store, sandbox gateway, marketplace catalog)
            // stubbed, a completed turn ending in "ack" is a genuine full-turn witness, not a
            // vacuously-satisfied assertion — if any real dependency were still live and unreachable,
            // the run would error out before the model ever replied.
            frames.ConcatText().Should().Contain("ack");
            promptTheModelReceived
                .Should()
                .NotBeNull(
                    "the scripted provider must have received a request to capture a prompt from; a "
                        + "null capture here means the turn errored before reaching the LLM — check "
                        + "for a SandboxSessionUnavailableException from the marketplace-catalog "
                        + "validation step, which is the exact failure this test was added to prevent"
                );

            promptTheModelReceived
                .Should()
                .Contain(
                    "Your workspace directory is:",
                    "a copy of the daemon mode still resolves caps.NeedsSandbox from its capability "
                        + "selection, so the real ApplyWorkspaceSuffix call site must still run for it"
                );

            // Non-vacuity: prove the test-owned fake is actually wired into the production DI graph
            // and consulted by it, rather than the test passing because the DI override was silently
            // dropped (see the poison-double and removed-override mutation notes in the commit this
            // test was added in). This does NOT isolate or pin
            // WorkspaceCatalogCompatibilityService.ValidateForSessionAsync as the caller: the copied
            // daemon mode enables subagents, and MarketplaceSubAgentLoader can reach the same
            // IMarketplaceCatalogClient, so CallCount > 0 is consistent with either call site (or
            // both) having run.
            catalogClient
                .CallCount.Should()
                .BeGreaterThan(
                    0,
                    "the fake catalog client must be consulted through the production DI graph (via "
                        + "WorkspaceCatalogCompatibilityService validation and/or "
                        + "MarketplaceSubAgentLoader), not silently bypassed"
                );
        }
        finally
        {
            // Best-effort temp cleanup (same idiom as ModeCapabilitiesCloneTests.Dispose) — a leftover
            // temp directory must never mask whatever the assertions above already reported.
            foreach (var dir in new[] { modeStoreDir, secretStoreDir })
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // A leftover temp directory is not worth failing a test over.
                }
            }
        }
    }

    /// <summary>
    /// Minimal <see cref="IMarketplaceCatalogClient"/> stub: always returns an empty-but-available
    /// catalog (never throws). The default workspace's <c>Marketplaces</c> list is empty (see
    /// <c>FileWorkspaceStore</c>), so an empty catalog is already compatible with it — this stub only
    /// needs to answer without throwing for
    /// <see cref="WorkspaceCatalogCompatibilityService.ValidateForSessionAsync"/> to succeed.
    /// <see cref="CallCount"/> lets the test prove this double was actually consulted by the
    /// production DI graph, rather than the DI override having been silently dropped; it does not
    /// isolate which caller reached it (see the call site for the caveat about a second caller in a
    /// mode with subagents enabled).
    /// </summary>
    private sealed class StubMarketplaceCatalogClient : IMarketplaceCatalogClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new MarketplaceCatalog(Selected: [], Marketplaces: []));
        }
    }

    /// <summary>
    /// Answers only the gateway calls the real agent-factory's sandbox branch makes on a first
    /// session: the health probe, session creation, and the post-create liveness probe (see
    /// <c>SandboxSessionRegistry.GetOrCreateLiveSessionAsync</c>, which calls
    /// <c>SandboxClient.GetAsync(sessionId)</c> — <c>GET api/v1/sandboxes/{sessionId}</c>, exactly two
    /// path segments and nothing after the id). Anything else — notably the root CLAUDE.md/AGENTS.md
    /// file read and any <c>.../sandboxes/{id}/files/...</c> workspace-file request — is left
    /// unanswered ON PURPOSE and must fail closed: <c>Program.cs</c>'s <c>TryBuildRootContextSuffix</c>
    /// catches a root-read failure and degrades to an empty seed rather than throwing, so this test
    /// does not need to model that endpoint's wire shape at all, and a stub that accidentally matched a
    /// file-read path could hide a real behavioral difference between "session is alive" and "this file
    /// exists" — see <see cref="StubSandboxGatewayHandler_DoesNotMatchWorkspaceFileReads"/>.
    /// </summary>
    private sealed class StubSandboxGatewayHandler : HttpMessageHandler
    {
        private string? _sessionId;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/health", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal))
            {
                _sessionId ??= "sess-" + Guid.NewGuid().ToString("N");
                return Task.FromResult(BuildSessionResponse(_sessionId));
            }

            // Exact match only: `SandboxClient.GetAsync` requests exactly
            // `api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}` with no trailing segments. A
            // `Contains` match here would also answer `.../sandboxes/{id}/files/{mount}?path=...`
            // (workspace file reads), silently treating "the session is alive" as "this file exists".
            if (
                request.Method == HttpMethod.Get
                && _sessionId is not null
                && path.EndsWith($"/api/v1/sandboxes/{Uri.EscapeDataString(_sessionId)}", StringComparison.Ordinal)
            )
            {
                return Task.FromResult(BuildSessionResponse(_sessionId));
            }

            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException($"Unhandled stub gateway request: {request.Method} {path}")
            );
        }

        private static HttpResponseMessage BuildSessionResponse(string sessionId) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new CreateSandboxResponseProbe(
                        SessionId: sessionId,
                        ContainerId: "container-1",
                        Volumes: new VolumesProbe(new WorkspaceVolumeProbe("/workspace", ReadOnly: false))
                    )
                ),
            };
    }

    /// <summary>
    /// Route-exactness pin for <see cref="StubSandboxGatewayHandler"/>: an unexpected
    /// <c>.../sandboxes/{id}/files/...</c> workspace-file request must still fail closed (the same
    /// exception the handler throws for any other unmodeled path), not be silently answered by the
    /// tightened liveness-probe match. Cheap and self-contained — no web app factory, no WebSocket turn.
    /// </summary>
    [Fact]
    public async Task StubSandboxGatewayHandler_DoesNotMatchWorkspaceFileReads()
    {
        var handler = new StubSandboxGatewayHandler();
        using var invoker = new HttpMessageInvoker(handler);

        // Establish a session id first, exactly as the real factory does, so the tightened match has a
        // real `_sessionId` to (correctly) decline to extend to a files sub-path.
        using var createResponse = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:3000/api/v1/sandboxes"),
            CancellationToken.None
        );
        var created = await createResponse.Content.ReadFromJsonAsync<CreateSandboxResponseProbe>();

        var filesRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://127.0.0.1:3000/api/v1/sandboxes/{created!.SessionId}/files/workspace?path=%2Ffoo"
        );

        var act = () => invoker.SendAsync(filesRequest, CancellationToken.None);

        await act.Should()
            .ThrowAsync<HttpRequestException>(
                "a workspace-file read is a different endpoint than the session-liveness probe and "
                    + "must not be silently answered by it"
            );
    }

    // Local mirrors of the registry's private snake_case JSON contract (same pattern as
    // SandboxSessionRegistryWorkspaceTests), used only to compose the stub gateway's responses.
    private sealed record CreateSandboxResponseProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
        [property: System.Text.Json.Serialization.JsonPropertyName("container_id")] string? ContainerId,
        [property: System.Text.Json.Serialization.JsonPropertyName("volumes")] VolumesProbe? Volumes
    );

    private sealed record VolumesProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("workspace")] WorkspaceVolumeProbe? Workspace
    );

    private sealed record WorkspaceVolumeProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("container_path")] string? ContainerPath,
        [property: System.Text.Json.Serialization.JsonPropertyName("read_only")] bool ReadOnly
    );
}
