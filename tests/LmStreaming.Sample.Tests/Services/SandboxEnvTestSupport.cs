using System.Net;
using System.Net.Http.Json;
using System.Text;

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
