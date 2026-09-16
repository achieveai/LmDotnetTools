namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins <see cref="SandboxSessionRegistry.EnsureSessionEnvAsync"/>: a session's env is confirmed once
/// against the gateway, then diffed against a caller's desired map on every call, and only the
/// difference is PATCHed — no redundant GET once the registry knows what is actually applied.
/// </summary>
public class SandboxSessionRegistryEnvTests
{
    /// <summary>
    /// The create-time seed records what this process ASKED for, so the first reconcile still confirms
    /// it against the gateway exactly once. It then costs nothing further: the confirmed map matches the
    /// desired one, so no PATCH is sent, and a second call does not GET again.
    /// </summary>
    [Fact]
    public async Task Create_WithEnv_SendsItOnCreate_ThenFirstEnsureConfirmsItWithOneGetAndNoPatch()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);
        // What a v0.1.11+ gateway reports back for a session created with FOO=1.
        captured.GetEnvBody = """{"env":{"FOO":"1"}}""";

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
        captured.GetEnvCalls.Should().Be(1);

        var second = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["FOO"] = "1" },
            "t2"
        );

        second.Should().Be(SandboxEnvApplyResult.Unchanged);
        captured.GetEnvCalls.Should().Be(1, "the confirmed map is cached; only the first call probes");
        captured.PatchedSessionIds.Should().BeEmpty();
    }

    /// <summary>
    /// THE F-001 REGRESSION. A pre-v0.1.11 gateway accepts the create and silently ignores <c>env</c>.
    /// While the create-time seed was trusted as applied, the first reconcile diffed the requested map
    /// against itself, returned <see cref="SandboxEnvApplyResult.Unchanged"/> without a single HTTP call,
    /// and so never reached either site that trips the sticky flag — leaving
    /// <see cref="SandboxSessionRegistry.SessionEnvSupported"/> reporting <see langword="true"/> forever,
    /// which the client reads to decide whether to OFFER env editing at all. Note the desired map here is
    /// IDENTICAL to what create sent: that is precisely the case that produced no network call.
    /// </summary>
    [Fact]
    public async Task Ensure_AfterCreateWithEnv_OnAGatewayWithNoEnvRoute_ReportsUnsupported()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws", Env: new Dictionary<string, string> { ["FOO"] = "1" })
        );

        // A bare 404 with no error_code body: the route itself does not exist.
        captured.GetEnvStatus = System.Net.HttpStatusCode.NotFound;
        captured.GetEnvBody = null;

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["FOO"] = "1" },
            "t1"
        );

        result.Should().Be(SandboxEnvApplyResult.Unsupported);
        registry.SessionEnvSupported.Should().BeFalse("the capability the client gates its editor on");
        captured.GetEnvCalls.Should().Be(1);
    }

    /// <summary>
    /// The confirming read is a CORRECTION, not the operation the caller asked for, and it runs inside
    /// agent construction. A gateway that is merely unwell — a 500, a timeout, a malformed body — must
    /// therefore not stop a conversation from starting: the create-time seed is used for this reconcile
    /// and the entry stays unconfirmed so the next activation retries. It is specifically NOT treated as
    /// "this gateway cannot do env", which only a 404-without-error_code or a 405 means.
    /// </summary>
    [Fact]
    public async Task Ensure_ConfirmingGetFailsUnexpectedly_FallsBackToTheCreateSeed_WithoutThrowingOrDisablingTheFeature()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws", Env: new Dictionary<string, string> { ["FOO"] = "1" })
        );

        captured.GetEnvStatus = System.Net.HttpStatusCode.InternalServerError;
        captured.GetEnvBody = null;

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["FOO"] = "1" },
            "t1"
        );

        result.Should().Be(SandboxEnvApplyResult.Unchanged, "the seed says FOO=1 and that is what was asked for");
        registry.SessionEnvSupported.Should().BeTrue("an unwell gateway is not an OLD gateway");
        captured.PatchedSessionIds.Should().BeEmpty();

        // Still unconfirmed, so the next activation probes again — and this time the gateway answers.
        captured.GetEnvStatus = System.Net.HttpStatusCode.OK;
        captured.GetEnvBody = """{"env":{"FOO":"1"}}""";

        var second = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["FOO"] = "1" },
            "t2"
        );

        second.Should().Be(SandboxEnvApplyResult.Unchanged);
        captured.GetEnvCalls.Should().Be(2);
    }

    [Fact]
    public async Task Ensure_DesiredDiffersFromCreated_Patches_TracksThread_AndSubsequentIdenticalCallIsUnchanged()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);
        captured.GetEnvBody = """{"env":{"FOO":"1","OLD":"o"}}""";
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

    /// <summary>
    /// The old-gateway degradation must live on the PATCH path too, not only on the confirming GET —
    /// a gateway that serves <c>GET /env</c> but not <c>PATCH /env</c> is a real shape, and before this
    /// was guarded the exception escaped <c>EnsureSessionEnvAsync</c> and surfaced as a 500 on workspace
    /// edit and a throw out of the agent build, which invokes this synchronously.
    /// </summary>
    [Fact]
    public async Task Ensure_PatchReturnsBare404_ReturnsUnsupported_RatherThanThrowing()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws"));
        captured.PatchEnvStatus = System.Net.HttpStatusCode.NotFound;

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["A"] = "1" },
            "t1"
        );

        result.Should().Be(SandboxEnvApplyResult.Unsupported);
        registry.SessionEnvSupported.Should().BeFalse();
        // The PATCH, not the GET, is what must have failed here.
        captured.PatchedSessionIds.Should().ContainSingle().Which.Should().Be(session.SessionId);
    }

    /// <summary>
    /// A 405 is the other shape an absent route takes — the path exists but not the verb, which a
    /// gateway serving GET /env without PATCH /env answers. It classifies as Protocol rather than
    /// NotFound, so a catch keyed only on NotFound let it escape.
    /// </summary>
    [Fact]
    public async Task Ensure_PatchReturns405_ReturnsUnsupported_RatherThanThrowing()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws"));
        captured.PatchEnvStatus = System.Net.HttpStatusCode.MethodNotAllowed;

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["A"] = "1" },
            "t1"
        );

        result.Should().Be(SandboxEnvApplyResult.Unsupported);
        registry.SessionEnvSupported.Should().BeFalse();
    }

    /// <summary>
    /// The over-refusal bound on the two tests above: a PATCH 404 that DOES name session_not_found is
    /// a missing session, not an old gateway, and must keep the feature enabled for every other
    /// session in the process. Without this, "return Unsupported on any 404" would pass both.
    /// </summary>
    [Fact]
    public async Task Ensure_PatchReturnsSessionNotFound_ReturnsSessionGone_AndSupportRemainsTrue()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws"));
        captured.PatchEnvStatus = System.Net.HttpStatusCode.NotFound;
        captured.PatchEnvJson = """{"error":"session not found","error_code":"session_not_found"}""";

        var result = await registry.EnsureSessionEnvAsync(
            session.SessionId,
            new Dictionary<string, string> { ["A"] = "1" },
            "t1"
        );

        result.Should().Be(SandboxEnvApplyResult.SessionGone);
        registry.SessionEnvSupported.Should().BeTrue();
    }

    /// <summary>
    /// THE F-003 REGRESSION. Sessions are shared per (workspace, app), so two conversations — or a turn
    /// overlapping a workspace edit — reconcile the SAME session id concurrently. The body is
    /// read-cache, diff, <c>await</c> PATCH, write-cache; interleaved, the second writer stores its
    /// result over the first's and the loser's map becomes <c>LastApplied</c>. That is not a stale
    /// timestamp that self-heals: the next diff is computed against a map the gateway never had, so it
    /// under- or over-patches until the session dies.
    /// <para>
    /// The assertion is that the reconciles were SERIALISED, which is the fix. Each handler holds itself
    /// open for a beat, and the two calls are dispatched on separate threads (the stub handler completes
    /// synchronously, so awaiting them in sequence on one thread could never overlap and would make this
    /// vacuous).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ensure_TwoConcurrentCallsForTheSameSession_AreSerialised()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws"));
        captured.EnvCallDelay = TimeSpan.FromMilliseconds(200);

        var first = Task.Run(() =>
            registry.EnsureSessionEnvAsync(session.SessionId, new Dictionary<string, string> { ["A"] = "1" }, "t1")
        );
        var second = Task.Run(() =>
            registry.EnsureSessionEnvAsync(session.SessionId, new Dictionary<string, string> { ["B"] = "2" }, "t2")
        );

        await Task.WhenAll(first, second);

        captured
            .MaxConcurrentEnvCalls.Should()
            .Be(1, "the read-diff-PATCH-write sequence for one session must not interleave");
    }

    /// <summary>
    /// The over-refusal bound on the test above: the per-session gate must not become a global one.
    /// Two DIFFERENT sessions reconciling at the same time are independent and must still overlap, or
    /// every conversation in the process would queue behind one slow gateway call.
    /// </summary>
    [Fact]
    public async Task Ensure_ConcurrentCallsForDifferentSessions_StillOverlap()
    {
        using var baseDir = new SandboxEnvTestSupport.TempWorkspaceBase();
        await using var registry = SandboxEnvTestSupport.CreateRegistry(baseDir.Path, out var captured);

        var a = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-a"));
        var b = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-b"));
        a.SessionId.Should().NotBe(b.SessionId);
        captured.EnvCallDelay = TimeSpan.FromMilliseconds(200);

        var first = Task.Run(() =>
            registry.EnsureSessionEnvAsync(a.SessionId, new Dictionary<string, string> { ["A"] = "1" }, "t1")
        );
        var second = Task.Run(() =>
            registry.EnsureSessionEnvAsync(b.SessionId, new Dictionary<string, string> { ["B"] = "2" }, "t2")
        );

        await Task.WhenAll(first, second);

        captured.MaxConcurrentEnvCalls.Should().Be(2, "distinct sessions must not contend");
    }
}
