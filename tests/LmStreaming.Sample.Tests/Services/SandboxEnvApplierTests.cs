using System.Collections.Immutable;
using System.Net;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Task 4 coverage for the real <see cref="SandboxEnvApplier"/>: the workspace &lt; mode &lt; provision
/// merge precedence (spec §5), the two reapply hooks (<see cref="SandboxEnvApplier.ReapplyForWorkspaceAsync"/>
/// / <see cref="SandboxEnvApplier.ReapplyForModeAsync"/>), and <see cref="SandboxEnvApplier.ApplyForThreadAsync"/>'s
/// resilience to an unsupported gateway and a transient transport failure.
/// </summary>
public class SandboxEnvApplierTests
{
    private static SandboxEnvApplier CreateApplier(
        SandboxSessionRegistry registry,
        InMemoryWorkspaceStoreFake? workspaces = null,
        InMemoryChatModeStoreFake? modes = null,
        InMemoryConversationStore? conversations = null
    ) =>
        new(
            workspaces ?? new InMemoryWorkspaceStoreFake(),
            modes ?? new InMemoryChatModeStoreFake(),
            conversations ?? new InMemoryConversationStore(),
            registry,
            NullLogger<SandboxEnvApplier>.Instance
        );

    private static Task SetModeAsync(InMemoryConversationStore conversations, string threadId, string modeId) =>
        conversations.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    MultiTurnAgentPool.ModePropertyKey,
                    modeId
                ),
            }
        );

    [Fact]
    public async Task ComputeEffectiveAsync_MergesWorkspaceThenModeThenProvision_LaterLayerWins()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out _);

        var workspaces = new InMemoryWorkspaceStoreFake();
        var modes = new InMemoryChatModeStoreFake();
        var conversations = new InMemoryConversationStore();
        var applier = CreateApplier(registry, workspaces, modes, conversations);

        workspaces.Seed(
            new Workspace
            {
                Id = "ws-1",
                Name = "WS",
                DirectoryRelPath = "ws1",
                Env = new Dictionary<string, string>
                {
                    ["A"] = "ws",
                    ["B"] = "ws",
                    ["C"] = "ws",
                },
            }
        );
        modes.Seed(
            new ChatMode
            {
                Id = "m",
                Name = "Mode",
                SystemPrompt = "prompt",
                Env = new Dictionary<string, string> { ["B"] = "mode", ["C"] = "mode" },
            }
        );
        await conversations.SaveMetadataAsync(
            "t1",
            new ThreadMetadata
            {
                ThreadId = "t1",
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.SetItem(
                    ConversationSandboxEnv.PropertyKey,
                    new Dictionary<string, string> { ["C"] = "prov" }
                ),
            }
        );

        var effective = await applier.ComputeEffectiveAsync("t1", "ws-1", "m", CancellationToken.None);

        effective
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, string>
                {
                    ["A"] = "ws",
                    ["B"] = "mode",
                    ["C"] = "prov",
                }
            );
    }

    /// <summary>
    /// The merged map is validated, not merely each layer as it was stored.
    /// <para>
    /// This is the case per-layer validation structurally cannot catch: a workspace holding <c>foo</c>
    /// and a mode holding <c>FOO</c> are each perfectly valid on their own, and Merge keeps them as
    /// two distinct Ordinal entries — but the gateway compares names case-insensitively and rejects
    /// the pair. It rejects it at CREATE, so the failure landed on the agent build and the
    /// conversation simply would not start, far from the edit that caused it and naming nothing the
    /// user could act on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ComputeEffectiveAsync_CrossLayerCaseDuplicate_ThrowsNamingBothKeys()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out _);

        var workspaces = new InMemoryWorkspaceStoreFake();
        var modes = new InMemoryChatModeStoreFake();
        var applier = CreateApplier(registry, workspaces, modes);

        workspaces.Seed(
            new Workspace
            {
                Id = "ws-1",
                Name = "WS",
                DirectoryRelPath = "ws1",
                Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["foo"] = "ws" },
            }
        );
        modes.Seed(
            new ChatMode
            {
                Id = "m",
                Name = "Mode",
                SystemPrompt = "prompt",
                Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["FOO"] = "mode" },
            }
        );

        var act = async () => await applier.ComputeEffectiveAsync("t1", "ws-1", "m", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<SandboxEnvValidationException>()).Which;
        ex.Keys.Should().Contain(["foo", "FOO"], "the collision belongs to the group, not to one side of it");
        ex.Layer.Should().Be("effective", "no single stored layer is at fault — each one is valid alone");
    }

    /// <summary>
    /// Over-refusal bound on the test above: layers that merge cleanly must still merge, so the new
    /// validation cannot be satisfied by simply rejecting every multi-layer map.
    /// </summary>
    [Fact]
    public async Task ComputeEffectiveAsync_ValidLayers_StillMerge()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out _);

        var workspaces = new InMemoryWorkspaceStoreFake();
        var modes = new InMemoryChatModeStoreFake();
        var applier = CreateApplier(registry, workspaces, modes);

        workspaces.Seed(
            new Workspace
            {
                Id = "ws-1",
                Name = "WS",
                DirectoryRelPath = "ws1",
                Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["FOO"] = "ws" },
            }
        );
        modes.Seed(
            new ChatMode
            {
                Id = "m",
                Name = "Mode",
                SystemPrompt = "prompt",
                Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["BAR"] = "mode" },
            }
        );

        var effective = await applier.ComputeEffectiveAsync("t1", "ws-1", "m", CancellationToken.None);

        effective.Should().BeEquivalentTo(new Dictionary<string, string> { ["FOO"] = "ws", ["BAR"] = "mode" });
    }

    [Fact]
    public async Task ReapplyForWorkspaceAsync_PatchesTheLastActivatedThreadsSession_WithTheNewWorkspaceEnv()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var workspaces = new InMemoryWorkspaceStoreFake();
        var modes = new InMemoryChatModeStoreFake();
        var conversations = new InMemoryConversationStore();
        var applier = CreateApplier(registry, workspaces, modes, conversations);

        modes.Seed(
            new ChatMode
            {
                Id = "m",
                Name = "Mode",
                SystemPrompt = "prompt",
            }
        );
        await SetModeAsync(conversations, "t1", "m");

        var oldEnv = new Dictionary<string, string> { ["A"] = "old" };
        workspaces.Seed(
            new Workspace
            {
                Id = "ws-1",
                Name = "WS",
                DirectoryRelPath = "ws1",
                Env = oldEnv,
            }
        );

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "ws1", Env: oldEnv));

        // "session created with {A:old}, ensured by thread t1": drive the same map through so the diff
        // is empty and only the last-activated-thread stamp changes.
        _ = await registry.EnsureSessionEnvAsync(session.SessionId, oldEnv, "t1");

        // The workspace env is edited.
        var newEnv = new Dictionary<string, string> { ["A"] = "new" };
        workspaces.Seed(
            new Workspace
            {
                Id = "ws-1",
                Name = "WS",
                DirectoryRelPath = "ws1",
                Env = newEnv,
            }
        );

        await applier.ReapplyForWorkspaceAsync("ws-1", CancellationToken.None);

        captured.PatchedSessionIds.Should().ContainSingle().Which.Should().Be(session.SessionId);
        captured.LastPatchBody.Should().NotBeNull();
        captured.LastPatchBody!.Value.GetProperty("A").GetString().Should().Be("new");
    }

    [Fact]
    public async Task ReapplyForModeAsync_PatchesOnlyTheSessionWhoseLastActivatedThreadRunsUnderThatMode()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var workspaces = new InMemoryWorkspaceStoreFake();
        var modes = new InMemoryChatModeStoreFake();
        var conversations = new InMemoryConversationStore();
        var applier = CreateApplier(registry, workspaces, modes, conversations);

        workspaces.Seed(
            new Workspace
            {
                Id = "w1",
                Name = "W1",
                DirectoryRelPath = "w1",
            }
        );
        workspaces.Seed(
            new Workspace
            {
                Id = "w2",
                Name = "W2",
                DirectoryRelPath = "w2",
            }
        );

        var sessionInMode = await registry.GetOrCreateSessionAsync(new WorkspaceRef("w1", "w1"));
        var sessionOtherMode = await registry.GetOrCreateSessionAsync(new WorkspaceRef("w2", "w2"));

        await SetModeAsync(conversations, "t-in", "m");
        await SetModeAsync(conversations, "t-other", "other");

        _ = await registry.EnsureSessionEnvAsync(sessionInMode.SessionId, new Dictionary<string, string>(), "t-in");
        _ = await registry.EnsureSessionEnvAsync(
            sessionOtherMode.SessionId,
            new Dictionary<string, string>(),
            "t-other"
        );

        modes.Seed(
            new ChatMode
            {
                Id = "m",
                Name = "Mode",
                SystemPrompt = "prompt",
                Env = new Dictionary<string, string> { ["M"] = "new" },
            }
        );

        await applier.ReapplyForModeAsync("m", CancellationToken.None);

        captured.PatchedSessionIds.Should().ContainSingle().Which.Should().Be(sessionInMode.SessionId);
    }

    [Fact]
    public async Task ApplyForThreadAsync_GatewayHasNoEnvRoute_DoesNotThrow_AndMarksSessionEnvUnsupported()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);
        var applier = CreateApplier(registry);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "ws1"));
        // Force EnsureSessionEnvAsync to fall back to a GET, as if the process had just restarted and
        // the create-time seed were gone.
        registry.ForgetSessionEnvStateForTests(session.SessionId);
        captured.GetEnvStatus = HttpStatusCode.NotFound;
        captured.GetEnvBody = null; // Bare 404, no error_code body -> "gateway has no env route at all".

        var act = async () =>
            await applier.ApplyForThreadAsync("t1", session.SessionId, "ws-1", "default", CancellationToken.None);

        await act.Should().NotThrowAsync();
        captured.PatchedSessionIds.Should().BeEmpty();
        registry.SessionEnvSupported.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyForThreadAsync_PatchTransportTimeout_DoesNotThrow()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var workspaces = new InMemoryWorkspaceStoreFake();
        var applier = CreateApplier(registry, workspaces);

        // Created with an empty env so the cache seeds to {}; the workspace's env (below) then differs,
        // forcing a non-empty diff and therefore a PATCH.
        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "ws1"));
        workspaces.Seed(
            new Workspace
            {
                Id = "ws-1",
                Name = "WS",
                DirectoryRelPath = "ws1",
                Env = new Dictionary<string, string> { ["A"] = "x" },
            }
        );
        captured.ThrowTransportErrorOnPatchEnv = true;

        var act = async () =>
            await applier.ApplyForThreadAsync("t1", session.SessionId, "ws-1", "default", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplyForThreadAsync_InvalidEnv_Rethrows()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var workspaces = new InMemoryWorkspaceStoreFake();
        var applier = CreateApplier(registry, workspaces);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "ws1"));
        workspaces.Seed(
            new Workspace
            {
                Id = "ws-1",
                Name = "WS",
                DirectoryRelPath = "ws1",
                Env = new Dictionary<string, string> { ["A"] = "x" },
            }
        );
        captured.PatchEnvStatus = HttpStatusCode.BadRequest;
        captured.PatchEnvJson = """{"error":"invalid_env","error_code":"invalid_env","keys":["A"]}""";

        var act = async () =>
            await applier.ApplyForThreadAsync("t1", session.SessionId, "ws-1", "default", CancellationToken.None);

        await act.Should().ThrowAsync<SandboxException>().Where(ex => ex.Kind == SandboxErrorKind.InvalidEnv);
    }
}
