using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Core;
using Serilog.Events;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// F-008: the sandbox env layers (workspace &lt; mode &lt; provision) reach the gateway THROUGH
/// <c>Program.cs</c>'s real agent factory and the per-turn activation seam in
/// <c>ChatWebSocketManager</c>, and are in effect before the agent's sandbox tool call runs.
/// <para>
/// Every other test of this machinery stops at a seam: <c>SandboxEnvApplierTests</c> drives the applier
/// directly, and the controller tests replace it with a recording double. None of them runs the agent
/// factory, so none could see the factory or the WebSocket turn path drop the call. Here the only fakes are
/// the gateway's HTTP surface, its MCP endpoint and the LLM; the env is read back off the fake gateway at
/// the instant the model's <c>Bash</c> call arrives there.
/// </para>
/// </summary>
public sealed class SandboxEnvAgentFactoryTests
{
    // Values are unique markers so the log scan below cannot match by accident.
    private const string WsValue = "ws-val-3f1c9";
    private const string ModeValue = "mode-val-8b27d";
    private const string ProvAValue = "prov-a-val-51e0a";
    private const string ProvBValue = "prov-b-val-c94e2";

    [Fact]
    public Task EnvFromAllThreeLayers_ReachesTheGatewayBeforeEachToolCall_AndFollowsTheLastActivatedThread() =>
        RunScenarioAsync(disableActivationReconcile: false);

    /// <summary>
    /// Non-vacuity switch for the per-turn seam. <see langword="true"/> swaps in an applier whose
    /// <c>ApplyForActivationAsync</c> does nothing; the scenario must then FAIL at thread A's second turn
    /// (the pooled agent is not rebuilt, so only activation can switch the shared session back). Not a
    /// test: flip the argument in the <see cref="FactAttribute"/> above to reproduce the RED run.
    /// </summary>
    private static async Task RunScenarioAsync(bool disableActivationReconcile)
    {
        var root = Path.Combine(Path.GetTempPath(), "lmstreaming-f008-" + Guid.NewGuid().ToString("N"));
        WebApplication? mcpApp = null;
        try
        {
            var gateway = new FakeGateway();
            var logSink = new CapturingSink();

            var responder = ScriptedSseResponder
                .New()
                .ForRole("workspace-agent", ctx => ctx.HasTool("Bash"))
                .Turn(t => t.ToolCall("Bash", new { command = "a-1" }))
                .Turn(t => t.Text("a-1 finished"))
                .Turn(t => t.ToolCall("Bash", new { command = "b-1" }))
                .Turn(t => t.Text("b-1 finished"))
                .Turn(t => t.ToolCall("Bash", new { command = "a-2" }))
                .Turn(t => t.Text("a-2 finished"))
                .Build();

            // Hermetic stores: nothing under AppContext.BaseDirectory is read or written.
            var modeStore = new FileChatModeStore(Path.Combine(root, "modes"));
            var workspaceStore = new FileWorkspaceStore(Path.Combine(root, "workspaces"), "default-leaf");
            var conversationStore = new FileConversationStore(Path.Combine(root, "conversations"));

            var workspace = await workspaceStore.CreateAsync(
                new WorkspaceCreate
                {
                    Name = "f008-env",
                    Env = new Dictionary<string, string> { ["W"] = WsValue, ["SHARED"] = WsValue },
                }
            );

            // A COPY of Workspace Agent: sandbox-capable through its capability selection, and editable.
            var copied = await modeStore.CopyModeAsync(SystemChatModes.WorkspaceAgentModeId, "F-008 env mode");
            var mode = await modeStore.UpdateModeAsync(
                copied.Id,
                new ChatModeCreateUpdate
                {
                    Name = copied.Name,
                    SystemPrompt = copied.SystemPrompt,
                    Env = new Dictionary<string, string> { ["M"] = ModeValue, ["SHARED"] = ModeValue },
                }
            );
            mode.EnabledCapabilityTools.Should().NotBeNullOrEmpty("the copy must keep its sandbox capability");

            var gatewayOptions = new SandboxGatewayOptions
            {
                BaseUrl = "http://127.0.0.1:39918",
                WorkspaceBasePath = null,
                Workspace = "default-leaf",
            };
            var gatewayLifetime = new SandboxGatewayLifetime(
                gatewayOptions,
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(gateway)
            );
            (mcpApp, var mcpHandler) = await StartInMemoryMcpServerAsync(new RecordingBashProvider(gateway));

            using var factory = new E2EWebAppFactory(
                "test-anthropic",
                new ScriptedBuilder(responder.AsAnthropicHandler()),
                configureServices: services =>
                {
                    services.RemoveAll<IChatModeStore>();
                    services.AddSingleton<IChatModeStore>(modeStore);
                    services.RemoveAll<IWorkspaceStore>();
                    services.AddSingleton<IWorkspaceStore>(workspaceStore);
                    services.RemoveAll<IConversationStore>();
                    services.AddSingleton<IConversationStore>(conversationStore);
                    services.RemoveAll<IRunLedgerStore>();
                    services.AddSingleton<IRunLedgerStore>(conversationStore);
                    services.RemoveAll<SandboxGatewayLifetime>();
                    services.AddSingleton(gatewayLifetime);
                    services.RemoveAll<SandboxSessionRegistry>();
                    // Built by the container (as Program.cs does) so its PATCH log line reaches the sink.
                    services.AddSingleton(sp => new SandboxSessionRegistry(
                        gatewayLifetime,
                        gatewayOptions,
                        sp.GetRequiredService<ILogger<SandboxSessionRegistry>>(),
                        new HttpClient(gateway),
                        new AuthOptions(),
                        new SessionSecretStore(Path.Combine(root, "secrets"), NullLogger<SessionSecretStore>.Instance)
                    ));
                    services.RemoveAll<IMarketplaceCatalogClient>();
                    services.AddSingleton<IMarketplaceCatalogClient>(new EmptyMarketplaceCatalogClient());
                    services.AddKeyedSingleton<HttpMessageHandler>(Program.SandboxMcpTransportHandlerKey, mcpHandler);
                    // Program.cs's Serilog pipeline reads sinks from DI (ReadFrom.Services).
                    services.AddSingleton<ILogEventSink>(logSink);
                    if (disableActivationReconcile)
                    {
                        services.RemoveAll<SandboxEnvApplier>();
                        services.AddSingleton<SandboxEnvApplier, NoActivationEnvApplier>();
                    }
                }
            );

            using var http = factory.CreateClient();
            var registry = factory.Services.GetRequiredService<SandboxSessionRegistry>();
            var threadA = await ProvisionAsync(http, workspace.Id, mode.Id, ProvAValue);
            var threadB = await ProvisionAsync(http, workspace.Id, mode.Id, ProvBValue);

            var expectedA = Expected(ProvAValue);
            var expectedB = Expected(ProvBValue);

            // --- Turn 1: thread A ------------------------------------------------------------------
            await using var clientA = new WebSocketTestClient(await factory.ConnectWebSocketAsync(threadA, mode.Id));
            await TakeTurnAsync(clientA, "run a-1", "a-1 finished");

            var sessionId = gateway.SessionId;
            sessionId.Should().NotBeNull("the real agent factory must have created a sandbox session");
            var a1 = gateway.SingleBash("a-1");
            a1.EnvAtCall.Should()
                .Equal(expectedA, "the merged workspace<mode<provision map must be live at the Bash call");
            gateway.CurrentEnv().Should().Equal(expectedA);

            // --- Turn 2: thread B, same workspace, therefore the same session ----------------------
            await using var clientB = new WebSocketTestClient(await factory.ConnectWebSocketAsync(threadB, mode.Id));
            await TakeTurnAsync(clientB, "run b-1", "b-1 finished");

            gateway.SessionsCreated.Should().Be(1, "both conversations share the workspace's one session");
            var b1 = gateway.SingleBash("b-1");
            b1.EnvAtCall.Should().Equal(expectedB, "the shared session must carry B's own map before B's tool call");
            gateway
                .IndexOfLastPatchBefore(b1.Sequence)
                .Should()
                .BeGreaterThan(a1.Sequence, "B's env PATCH lands between A's tool call and B's tool call");
            registry.TryGetLastActivatedThread(sessionId!, out var lastAfterB).Should().BeTrue();
            lastAfterB.Should().Be(threadB, "the shared session follows the most recently activated thread");

            // --- Turn 3: thread A again, on its POOLED agent (not rebuilt) --------------------------
            await TakeTurnAsync(clientA, "run a-2", "a-2 finished");

            var a2 = gateway.SingleBash("a-2");
            a2.EnvAtCall.Should()
                .Equal(
                    expectedA,
                    "only the per-turn activation reconcile can switch the shared session back to A before "
                        + "A's next tool call; the agent-build apply does not run again"
                );
            gateway.IndexOfLastPatchBefore(a2.Sequence).Should().BeGreaterThan(b1.Sequence);
            registry.TryGetLastActivatedThread(sessionId!, out var lastAfterA2).Should().BeTrue();
            lastAfterA2.Should().Be(threadA);

            responder.RemainingTurns["workspace-agent"].Should().Be(0, "every scripted turn was consumed in order");

            // --- Privacy: no env VALUE in any captured log event ------------------------------------
            logSink.Events.Should().NotBeEmpty("the DI log sink must be wired, or the scan below is vacuous");
            logSink
                .Events.Should()
                .Contain(
                    e => e.Contains("env patched by thread", StringComparison.Ordinal),
                    "the registry's PATCH log line (the one that names keys) must be among the scanned events"
                );
            foreach (var value in new[] { WsValue, ModeValue, ProvAValue, ProvBValue })
            {
                logSink.Events.Should().NotContain(e => e.Contains(value, StringComparison.Ordinal));
            }
        }
        finally
        {
            if (mcpApp is not null)
            {
                await mcpApp.DisposeAsync();
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static Dictionary<string, string> Expected(string provisionValue) =>
        new()
        {
            ["W"] = WsValue,
            ["M"] = ModeValue,
            ["P"] = provisionValue,
            ["SHARED"] = provisionValue,
        };

    /// <summary>Provisions a conversation through the real REST endpoint, provision env included.</summary>
    private static async Task<string> ProvisionAsync(
        HttpClient http,
        string workspaceId,
        string modeId,
        string provValue
    )
    {
        using var response = await http.PostAsJsonAsync(
            "/api/conversations",
            new
            {
                workspaceId,
                providerId = "test-anthropic",
                modeId,
                env = new Dictionary<string, string> { ["P"] = provValue, ["SHARED"] = provValue },
            }
        );
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "provision must succeed; body was: {0}", body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("threadId").GetString()!;
    }

    private static async Task TakeTurnAsync(WebSocketTestClient client, string message, string expectedText)
    {
        await client.SendUserMessageAsync(message);
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));
        frames.ConcatText().Should().Contain(expectedText, "the turn must complete, tool call included");
    }

    private static async Task<(WebApplication App, HttpMessageHandler Handler)> StartInMemoryMcpServerAsync(
        IFunctionProvider provider
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(provider);
        builder.Services.AddMcpServerFromFunctionProviders();

        var app = builder.Build();
        app.MapMcpFunctionProviders();
        await app.StartAsync();

        var testServer = (TestServer)app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        return (app, testServer.CreateHandler());
    }

    /// <summary>One gateway-observed event, in arrival order.</summary>
    private sealed record GatewayEvent(
        int Sequence,
        string Kind,
        string? Command,
        Dictionary<string, string> EnvAtCall
    );

    /// <summary>
    /// A stateful fake of the gateway's lifecycle + session-env HTTP surface (same wire shapes as
    /// <c>SandboxEnvTestSupport</c>): create seeds the session map from the request's <c>env</c>, GET
    /// answers it, PATCH applies the flat diff (null unsets). Every request and every tool call is appended
    /// to one ordered log. Unmodelled routes fail closed.
    /// </summary>
    private sealed class FakeGateway : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
        private readonly List<GatewayEvent> _events = [];

        public string? SessionId { get; private set; }

        public int SessionsCreated { get; private set; }

        public Dictionary<string, string> CurrentEnv()
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_env, StringComparer.Ordinal);
            }
        }

        public void RecordBash(string? command)
        {
            lock (_gate)
            {
                AddEvent("bash", command);
            }
        }

        public GatewayEvent SingleBash(string command)
        {
            lock (_gate)
            {
                return _events.Should().ContainSingle(e => e.Kind == "bash" && e.Command == command).Subject;
            }
        }

        /// <summary>Sequence number of the last PATCH strictly before <paramref name="sequence"/>, or -1.</summary>
        public int IndexOfLastPatchBefore(int sequence)
        {
            lock (_gate)
            {
                return _events.LastOrDefault(e => e.Kind == "patch" && e.Sequence < sequence)?.Sequence ?? -1;
            }
        }

        private void AddEvent(string kind, string? command = null) =>
            _events.Add(
                new GatewayEvent(
                    _events.Count,
                    kind,
                    command,
                    new Dictionary<string, string>(_env, StringComparer.Ordinal)
                )
            );

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (path.EndsWith("/health", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                if (request.Method == HttpMethod.Post && path.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal))
                {
                    if (SessionId is null)
                    {
                        SessionId = "sess-" + Guid.NewGuid().ToString("N");
                        SessionsCreated++;
                        using var doc = JsonDocument.Parse(body!);
                        if (doc.RootElement.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var entry in env.EnumerateObject())
                            {
                                _env[entry.Name] = entry.Value.GetString()!;
                            }
                        }

                        AddEvent("create");
                    }

                    return SessionResponse(SessionId);
                }

                var sessionPrefix = SessionId is null ? null : $"/api/v1/sandboxes/{Uri.EscapeDataString(SessionId)}";

                if (sessionPrefix is not null && path.EndsWith(sessionPrefix + "/env", StringComparison.Ordinal))
                {
                    if (request.Method == HttpMethod.Patch)
                    {
                        using var doc = JsonDocument.Parse(body!);
                        foreach (var entry in doc.RootElement.EnumerateObject())
                        {
                            if (entry.Value.ValueKind == JsonValueKind.Null)
                            {
                                _ = _env.Remove(entry.Name);
                            }
                            else
                            {
                                _env[entry.Name] = entry.Value.GetString()!;
                            }
                        }

                        AddEvent("patch");
                    }
                    else
                    {
                        AddEvent("get-env");
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { env = _env }),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                if (
                    request.Method == HttpMethod.Get
                    && sessionPrefix is not null
                    && path.EndsWith(sessionPrefix, StringComparison.Ordinal)
                )
                {
                    return SessionResponse(SessionId!);
                }
            }

            throw new HttpRequestException($"Unhandled fake gateway request: {request.Method} {path}");
        }

        private static HttpResponseMessage SessionResponse(string sessionId) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new CreateSandboxResponseProbe(
                        sessionId,
                        "container-1",
                        new VolumesProbe(new WorkspaceVolumeProbe("/workspace", ReadOnly: false))
                    )
                ),
            };
    }

    /// <summary>The sandbox MCP server's <c>Bash</c> tool: records the call, with the env live at that instant.</summary>
    private sealed class RecordingBashProvider(FakeGateway gateway) : IFunctionProvider
    {
        public string ProviderName => "fake-sandbox";

        public int Priority => 0;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
            [
                new FunctionDescriptor
                {
                    ProviderName = ProviderName,
                    Contract = new FunctionContract
                    {
                        Name = "Bash",
                        Description = "Runs a shell command in the sandbox.",
                        Parameters =
                        [
                            new FunctionParameterContract
                            {
                                Name = "command",
                                ParameterType = JsonSchemaObject.String("The command."),
                                IsRequired = true,
                            },
                        ],
                    },
                    Handler = (argsJson, _, _) =>
                    {
                        using var doc = JsonDocument.Parse(argsJson);
                        gateway.RecordBash(doc.RootElement.TryGetProperty("command", out var c) ? c.GetString() : null);
                        return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));
                    },
                },
            ];
    }

    /// <summary>Empty, available marketplace catalog: compatible with a workspace that selects none.</summary>
    private sealed class EmptyMarketplaceCatalogClient : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) => Task.FromResult(new MarketplaceCatalog(Selected: [], Marketplaces: []));
    }

    /// <summary>Mutation double: the real applier with the per-turn activation reconcile removed.</summary>
    private sealed class NoActivationEnvApplier(
        IWorkspaceStore workspaces,
        IChatModeStore modes,
        IConversationStore conversations,
        SandboxSessionRegistry registry,
        ILogger<SandboxEnvApplier> logger
    ) : SandboxEnvApplier(workspaces, modes, conversations, registry, logger)
    {
        public override Task ApplyForActivationAsync(string threadId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Captures every rendered Serilog event plus its property values, for the no-values scan.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<string> _events = [];

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            var text = new StringBuilder(logEvent.RenderMessage());
            foreach (var property in logEvent.Properties)
            {
                text.Append(' ').Append(property.Key).Append('=').Append(property.Value);
            }

            if (logEvent.Exception is not null)
            {
                text.Append(' ').Append(logEvent.Exception);
            }

            lock (_events)
            {
                _events.Add(text.ToString());
            }
        }
    }

    // Local mirrors of the registry's private snake_case create-response contract.
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
