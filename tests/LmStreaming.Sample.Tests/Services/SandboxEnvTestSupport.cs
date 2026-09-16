using System.Net;
using System.Net.Http.Json;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Shared registry + stub-handler builder for sandbox per-session-env tests (Task 2's
/// <c>SandboxSessionRegistry.EnsureSessionEnvAsync</c> and Task 4's higher-level consumers). Extends the
/// bare create-only stub from <see cref="SandboxSessionRegistryWorkspaceTests"/> with the session-scoped
/// direct-API env routes (<c>GET</c>/<c>PATCH .../api/v1/sandboxes/{sessionId}/env</c>) so a test can
/// drive a full create → ensure-env round trip against one in-memory registry.
/// </summary>
internal static class SandboxEnvTestSupport
{
    internal const string DefaultLeaf = "default-leaf";

    /// <summary>
    /// Builds a registry wired to the stub handler below. <paramref name="workspaceBasePath"/> mirrors
    /// <see cref="SandboxSessionRegistryWorkspaceTests"/>'s helper of the same shape — a real temp
    /// directory so <c>ResolveWorkspace</c>'s containment check has a parent to resolve against.
    /// </summary>
    internal static SandboxSessionRegistry CreateRegistry(string workspaceBasePath, out CapturedEnvRequests captured)
    {
        var capturedRequests = new CapturedEnvRequests();
        captured = capturedRequests;

        // Gateway lifetime: any 200 makes EnsureReadyAsync adopt it as healthy (it probes /health). This
        // uses its OWN HttpClient/handler, deliberately separate from the registry's — mirrors the
        // workspace-tests pattern.
        static HttpResponseMessage Healthy(HttpRequestMessage _) => new(HttpStatusCode.OK);

        var options = new SandboxGatewayOptions
        {
            BaseUrl = "http://localhost:3000",
            WorkspaceBasePath = workspaceBasePath,
            Workspace = DefaultLeaf,
        };

        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(Healthy))
        );

        return new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(request => Respond(request, capturedRequests))),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request, CapturedEnvRequests captured)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (request.Method == HttpMethod.Post && path.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal))
        {
            var bodyJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            captured.LastCreateBody = JsonDocument.Parse(bodyJson).RootElement.Clone();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new CreateSandboxResponseProbe(
                        SessionId: "sess-" + Guid.NewGuid().ToString("N"),
                        ContainerId: "container-1",
                        Volumes: new VolumesProbe(new WorkspaceVolumeProbe("/workspace", ReadOnly: false))
                    )
                ),
            };
        }

        if (path.EndsWith("/env", StringComparison.Ordinal))
        {
            // Path shape: /api/v1/sandboxes/{sessionId}/env — the session id is the second-to-last segment.
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var sessionId = segments[^2];

            if (request.Method == HttpMethod.Get)
            {
                captured.GetEnvCalls++;
                return new HttpResponseMessage(captured.GetEnvStatus)
                {
                    Content = captured.GetEnvBody is null
                        ? null
                        : new StringContent(captured.GetEnvBody, Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Patch)
            {
                if (captured.ThrowTransportErrorOnPatchEnv)
                {
                    // SandboxClient.Transport.cs maps an HttpRequestException thrown by the inner handler
                    // to SandboxException(TransportTimeout) ("could not reach the sandbox gateway") — the
                    // simplest deterministic way to provoke that kind without a real timing budget.
                    throw new HttpRequestException("Simulated transport failure for PATCH .../env.");
                }

                var bodyJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                captured.LastPatchBody = JsonDocument.Parse(bodyJson).RootElement.Clone();
                captured.PatchedSessionIds.Add(sessionId);

                return new HttpResponseMessage(captured.PatchEnvStatus)
                {
                    Content = new StringContent(captured.PatchEnvJson, Encoding.UTF8, "application/json"),
                };
            }
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    /// <summary>Records every create/GET-env/PATCH-env request the stub handler saw, and lets a test
    /// configure the GET/PATCH env responses before exercising the registry.</summary>
    internal sealed class CapturedEnvRequests
    {
        /// <summary>The most recent sandbox-create request body (the whole JSON object, including <c>env</c>).</summary>
        public JsonElement? LastCreateBody { get; set; }

        /// <summary>The most recent PATCH .../env request body (flat: key → value, or JSON null for unset).</summary>
        public JsonElement? LastPatchBody { get; set; }

        /// <summary>Every session id a PATCH .../env request targeted, in call order.</summary>
        public List<string> PatchedSessionIds { get; } = [];

        /// <summary>Number of GET .../env requests observed so far.</summary>
        public int GetEnvCalls { get; set; }

        /// <summary>Status the next GET .../env response returns. Default <see cref="HttpStatusCode.OK"/>.</summary>
        public HttpStatusCode GetEnvStatus { get; set; } = HttpStatusCode.OK;

        /// <summary>Body the next GET .../env response returns. <see langword="null"/> sends no content
        /// (a bare 404 with no gateway error body).</summary>
        public string? GetEnvBody { get; set; } = """{"env":{}}""";

        /// <summary>Status the next PATCH .../env response returns. Default <see cref="HttpStatusCode.OK"/>.</summary>
        public HttpStatusCode PatchEnvStatus { get; set; } = HttpStatusCode.OK;

        /// <summary>Body the next PATCH .../env response returns — the resulting full env map.</summary>
        public string PatchEnvJson { get; set; } = """{"env":{}}""";

        /// <summary>When true, the next PATCH .../env request throws <see cref="HttpRequestException"/>
        /// instead of responding — the SDK maps that to <c>SandboxErrorKind.TransportTimeout</c>.</summary>
        public bool ThrowTransportErrorOnPatchEnv { get; set; }
    }

    // Local mirrors of the registry's private snake_case JSON contract — same probes
    // SandboxSessionRegistryWorkspaceTests uses to compose the gateway's create response.
    internal sealed record CreateSandboxResponseProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
        [property: System.Text.Json.Serialization.JsonPropertyName("container_id")] string? ContainerId,
        [property: System.Text.Json.Serialization.JsonPropertyName("volumes")] VolumesProbe? Volumes
    );

    internal sealed record VolumesProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("workspace")] WorkspaceVolumeProbe? Workspace
    );

    internal sealed record WorkspaceVolumeProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("container_path")] string? ContainerPath,
        [property: System.Text.Json.Serialization.JsonPropertyName("read_only")] bool ReadOnly
    );

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Creates (and deletes) a real temp directory to serve as the workspace base.</summary>
    internal sealed class TempWorkspaceBase : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ws-env-test-" + Guid.NewGuid().ToString("N"));

        public TempWorkspaceBase() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup; a leaked temp dir must not fail the test.
            }
        }
    }
}

/// <summary>
/// Minimal in-memory <see cref="IWorkspaceStore"/> fake for <see cref="SandboxEnvApplier"/> tests and
/// <see cref="NoOpSandboxEnvApplier"/> construction. Supports only what those callers need — seeding
/// and lookup by id — so the mutation members throw rather than silently no-op.
/// </summary>
internal sealed class InMemoryWorkspaceStoreFake : IWorkspaceStore
{
    private readonly Dictionary<string, Workspace> _workspaces = new(StringComparer.Ordinal);

    public void Seed(Workspace workspace) => _workspaces[workspace.Id] = workspace;

    public Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Workspace>>([.. _workspaces.Values]);

    public Task<Workspace?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_workspaces.TryGetValue(id, out var workspace) ? workspace : null);

    public Task<Workspace> CreateAsync(WorkspaceCreate dto, CancellationToken ct = default) =>
        throw new NotSupportedException("InMemoryWorkspaceStoreFake supports only Seed/GetAsync.");

    public Task<Workspace> UpdateAsync(string id, WorkspaceUpdate dto, CancellationToken ct = default) =>
        throw new NotSupportedException("InMemoryWorkspaceStoreFake supports only Seed/GetAsync.");
}

/// <summary>Minimal in-memory <see cref="IChatModeStore"/> fake — see <see cref="InMemoryWorkspaceStoreFake"/>.</summary>
internal sealed class InMemoryChatModeStoreFake : IChatModeStore
{
    private readonly Dictionary<string, ChatMode> _modes = new(StringComparer.Ordinal);

    public void Seed(ChatMode mode) => _modes[mode.Id] = mode;

    public Task<IReadOnlyList<ChatMode>> GetAllModesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChatMode>>([.. _modes.Values]);

    public Task<ChatMode?> GetModeAsync(string modeId, CancellationToken ct = default) =>
        Task.FromResult(_modes.TryGetValue(modeId, out var mode) ? mode : null);

    public Task<ChatMode> CreateModeAsync(ChatModeCreateUpdate mode, CancellationToken ct = default) =>
        throw new NotSupportedException("InMemoryChatModeStoreFake supports only Seed/GetModeAsync.");

    public Task<ChatMode> UpdateModeAsync(string modeId, ChatModeCreateUpdate mode, CancellationToken ct = default) =>
        throw new NotSupportedException("InMemoryChatModeStoreFake supports only Seed/GetModeAsync.");

    public Task DeleteModeAsync(string modeId, CancellationToken ct = default) =>
        throw new NotSupportedException("InMemoryChatModeStoreFake supports only Seed/GetModeAsync.");

    public Task<ChatMode> CopyModeAsync(string modeId, string newName, CancellationToken ct = default) =>
        throw new NotSupportedException("InMemoryChatModeStoreFake supports only Seed/GetModeAsync.");
}

/// <summary>
/// No-op <see cref="SandboxEnvApplier"/> double for controller/persistence tests that need a valid
/// applier instance to satisfy DI but exercise no env-reapply behavior at all. Both public reapply
/// hooks are overridden to a no-op, so the base class's real dependencies (wired here to harmless
/// throwaway fakes and one shared, never-invoked registry) are never actually exercised.
/// </summary>
internal class NoOpSandboxEnvApplier : SandboxEnvApplier
{
    // Shared across every instance: the underlying registry is never invoked (both reapply methods are
    // overridden below), so one lazily-built throwaway instance is enough for the whole test run rather
    // than standing up a gateway/HttpClient/temp-dir per controller test.
    private static readonly Lazy<SandboxSessionRegistry> SharedRegistry = new(() =>
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sandbox-env-noop-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        return SandboxEnvTestSupport.CreateRegistry(dir, out _);
    });

    public NoOpSandboxEnvApplier()
        : base(
            new InMemoryWorkspaceStoreFake(),
            new InMemoryChatModeStoreFake(),
            new InMemoryConversationStore(),
            SharedRegistry.Value,
            NullLogger<SandboxEnvApplier>.Instance
        ) { }

    public override Task ReapplyForWorkspaceAsync(string workspaceId, CancellationToken ct) => Task.CompletedTask;

    public override Task ReapplyForModeAsync(string modeId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// A <see cref="SandboxEnvApplier"/> whose mode reapply fails the way a GATEWAY rejection fails —
/// after the store write has already succeeded. Used to pin the controllers' partial-success
/// <c>invalid_env</c> response, which is a different situation from a validator rejection: there,
/// nothing was persisted; here, the mode was saved and only the env was refused.
/// </summary>
internal sealed class GatewayRejectsEnvApplier(params string[] invalidKeys) : NoOpSandboxEnvApplier
{
    public override Task ReapplyForModeAsync(string modeId, CancellationToken ct) =>
        throw new SandboxException(SandboxErrorKind.InvalidEnv, "gateway refused the environment", 400)
        {
            ErrorCode = "invalid_env",
            InvalidKeys = invalidKeys,
        };
}
