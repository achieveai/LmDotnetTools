using System.Net;
using System.Net.Http.Json;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the load-bearing workspace-picker AC: a session created for a SELECTED workspace mounts
/// that workspace's own directory leaf (resolved via <see cref="SandboxGatewayOptions.ResolveWorkspace(string?)"/>),
/// NOT the configured default leaf. Also covers the default-leaf fallback (null directory) and the
/// first-creation-wins cache contract documented on
/// <see cref="SandboxSessionRegistry.GetOrCreateSessionAsync(WorkspaceRef, System.Threading.CancellationToken)"/>.
/// </summary>
public class SandboxSessionRegistryWorkspaceTests
{
    private const string DefaultLeaf = "default-leaf";

    [Fact]
    public async Task GetOrCreateSession_SelectedWorkspace_MountsItsOwnLeaf_NotTheDefault()
    {
        using var baseDir = new TempWorkspaceBase();
        await using var registry = CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "projA"));

        // The session mounts the SELECTED workspace's directory, not the configured default leaf.
        session.WorkspaceRelPath.Should().Be("projA");
        session.WorkspaceRelPath.Should().NotBe(DefaultLeaf);
        session.WorkspaceId.Should().Be("ws-1");

        // The leaf the registry computed is also what it sent to the gateway as the `workspace` field.
        captured.LastWorkspace.Should().Be("projA");
    }

    [Fact]
    public async Task GetOrCreateSession_NullDirectory_FallsBackToConfiguredDefaultLeaf()
    {
        using var baseDir = new TempWorkspaceBase();
        await using var registry = CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-2", null));

        session.WorkspaceRelPath.Should().Be(DefaultLeaf);
        captured.LastWorkspace.Should().Be(DefaultLeaf);
    }

    [Fact]
    public async Task GetOrCreateSession_FirstCreationWins_SecondCallReturnsCachedSession()
    {
        using var baseDir = new TempWorkspaceBase();
        await using var registry = CreateRegistry(baseDir.Path, out _);

        var first = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "projA"));
        // A later caller for the same id but a DIFFERENT directory must get the cached session —
        // the cache is keyed by id and the first creation wins (the second directory is ignored).
        var second = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "projB"));

        second.Should().BeSameAs(first);
        second.WorkspaceRelPath.Should().Be("projA");
    }

    [Fact]
    public async Task GetOrCreateSession_DoesNotCreateWorkspaceDirectoryOnDisk_GatewayOwnsIt()
    {
        // The client must NEVER touch the workspace filesystem — the gateway may be on a remote
        // machine and owns workspace directory creation (create-if-missing) and mounting. Even with a
        // local base configured, creating a session forwards the leaf WITHOUT the client mkdir-ing it.
        using var baseDir = new TempWorkspaceBase();
        await using var registry = CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-fs", "projFS"));

        session.WorkspaceRelPath.Should().Be("projFS");
        captured.LastWorkspace.Should().Be("projFS"); // leaf still forwarded to the gateway
        Directory.Exists(System.IO.Path.Combine(baseDir.Path, "projFS"))
            .Should().BeFalse("the client must not create the workspace directory — the gateway owns it");
    }

    private static SandboxSessionRegistry CreateRegistry(string workspaceBasePath, out CapturedRequest captured)
    {
        var capturedRequest = new CapturedRequest();
        captured = capturedRequest;

        // Gateway lifetime: any 200 makes EnsureReadyAsync adopt it as healthy (it probes /health).
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
            new HttpClient(new StubHandler(Healthy)));

        // Registry client: capture the create request's `workspace` leaf and return a valid
        // CreateSandboxResponse so creation succeeds.
        HttpResponseMessage CreateSession(HttpRequestMessage request)
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal))
            {
                var body = request.Content!.ReadFromJsonAsync<CreateSandboxRequestProbe>().GetAwaiter().GetResult();
                capturedRequest.LastWorkspace = body?.Workspace;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new CreateSandboxResponseProbe(
                        SessionId: "sess-" + Guid.NewGuid().ToString("N"),
                        ContainerId: "container-1",
                        Volumes: new VolumesProbe(new WorkspaceVolumeProbe("/workspace", ReadOnly: false)))),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        return new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(CreateSession)),
            new AuthOptions(),
            new AuthSharedSecret(new AuthOptions()));
    }

    private sealed class CapturedRequest
    {
        public string? LastWorkspace { get; set; }
    }

    /// <summary>Creates (and deletes) a real temp directory to serve as the workspace base, so the
    /// <c>ResolveWorkspace</c> containment check has a real parent to resolve leaves under. The
    /// registry itself never creates the per-workspace leaf (the gateway owns that).</summary>
    private sealed class TempWorkspaceBase : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ws-test-" + Guid.NewGuid().ToString("N"));

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

    // Local mirrors of the registry's private snake_case JSON contract, used only to compose the
    // gateway's create response and to read back the `workspace` field the registry sent. The
    // property names are tagged explicitly so they bind regardless of default naming policy.
    private sealed record CreateSandboxRequestProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("workspace")] string? Workspace);

    private sealed record CreateSandboxResponseProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
        [property: System.Text.Json.Serialization.JsonPropertyName("container_id")] string? ContainerId,
        [property: System.Text.Json.Serialization.JsonPropertyName("volumes")] VolumesProbe? Volumes);

    private sealed record VolumesProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("workspace")] WorkspaceVolumeProbe? Workspace);

    private sealed record WorkspaceVolumeProbe(
        [property: System.Text.Json.Serialization.JsonPropertyName("container_path")] string? ContainerPath,
        [property: System.Text.Json.Serialization.JsonPropertyName("read_only")] bool ReadOnly);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
