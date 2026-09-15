namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins <see cref="SandboxSessionRegistry.EnsureSessionEnvAsync"/>: a session's env is seeded from
/// create, diffed against a caller's desired map on every call, and only the difference is PATCHed —
/// never a redundant GET once the registry already knows what is applied.
/// </summary>
public class SandboxSessionRegistryEnvTests
{
    [Fact]
    public async Task Create_WithEnv_SendsItOnCreate_AndSeedsCacheSoEnsureIsUnchangedWithNoRoundTrip()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws", Env: new Dictionary<string, string> { ["FOO"] = "1" })
        );

        captured.LastCreateBody!.Value.GetProperty("env").GetProperty("FOO").GetString().Should().Be("1");

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["FOO"] = "1" },
            "t1"
        );

        result.Should().Be(SandboxEnvApplyResult.Unchanged);
        captured.PatchedSessionIds.Should().BeEmpty();
        captured.GetEnvCalls.Should().Be(0);
    }

    [Fact]
    public async Task Ensure_DesiredDiffersFromCreated_Patches_TracksThread_AndSubsequentIdenticalCallIsUnchanged()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);
        captured.PatchEnvJson = """{"env":{"FOO":"2","NEW":"n"}}""";

        var session = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws", Env: new Dictionary<string, string> { ["FOO"] = "1", ["OLD"] = "o" })
        );

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["FOO"] = "2", ["NEW"] = "n" },
            "t2"
        );

        result.Should().Be(SandboxEnvApplyResult.Patched);
        captured.PatchedSessionIds.Should().Equal(session.SessionId);
        captured.LastPatchBody!.Value.GetProperty("FOO").GetString().Should().Be("2");
        captured.LastPatchBody!.Value.GetProperty("NEW").GetString().Should().Be("n");
        captured.LastPatchBody!.Value.GetProperty("OLD").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

        registry.TryGetLastActivatedThread(session.SessionId, out var threadId).Should().BeTrue();
        threadId.Should().Be("t2");

        // The cache now reflects the PATCH RESPONSE ({"FOO":"2","NEW":"n"} — OLD is gone), so an identical
        // second ensure call diffs to empty without issuing another PATCH.
        var second = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["FOO"] = "2", ["NEW"] = "n" },
            "t3"
        );

        second.Should().Be(SandboxEnvApplyResult.Unchanged);
        captured.PatchedSessionIds.Should().Equal(session.SessionId); // still just the one PATCH
    }

    [Fact]
    public async Task Ensure_NoCacheEntry_FallsBackToOneGet_ThenPatchesOnlyTheDifference()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws"));
        registry.ForgetSessionEnvStateForTests(session.SessionId);
        captured.GetEnvBody = """{"env":{"A":"1"}}""";

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
            "t1"
        );

        result.Should().Be(SandboxEnvApplyResult.Patched);
        captured.GetEnvCalls.Should().Be(1);
        captured
            .LastPatchBody!.Value.TryGetProperty("A", out _)
            .Should()
            .BeFalse("A is unchanged from the GET, not part of the diff");
        captured.LastPatchBody!.Value.GetProperty("B").GetString().Should().Be("2");
    }

    [Fact]
    public async Task Ensure_NoCacheEntry_GetReturnsBare404_ReturnsUnsupported_AndSticksWithoutFurtherGet()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws"));
        registry.ForgetSessionEnvStateForTests(session.SessionId);
        captured.GetEnvStatus = System.Net.HttpStatusCode.NotFound;
        captured.GetEnvBody = null;

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["A"] = "1" },
            "t1"
        );

        result.Should().Be(SandboxEnvApplyResult.Unsupported);
        registry.SessionEnvSupported.Should().BeFalse();
        captured.PatchedSessionIds.Should().BeEmpty();
        captured.GetEnvCalls.Should().Be(1);

        var second = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["A"] = "1" },
            "t2"
        );

        second.Should().Be(SandboxEnvApplyResult.Unsupported);
        captured.GetEnvCalls.Should().Be(1, "the sticky Unsupported flag must short-circuit before another GET");
    }

    [Fact]
    public async Task Ensure_NoCacheEntry_GetReturnsSessionNotFound_ReturnsSessionGone_AndSupportRemainsTrue()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws"));
        registry.ForgetSessionEnvStateForTests(session.SessionId);
        captured.GetEnvStatus = System.Net.HttpStatusCode.NotFound;
        captured.GetEnvBody = """{"error":"session not found","error_code":"session_not_found"}""";

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["A"] = "1" },
            "t1"
        );

        result.Should().Be(SandboxEnvApplyResult.SessionGone);
        registry.SessionEnvSupported.Should().BeTrue();
    }
}
