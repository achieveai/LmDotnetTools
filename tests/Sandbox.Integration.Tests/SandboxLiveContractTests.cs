using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// The live pinned-gateway contract matrix. Every test drives the real SDK against a real gateway
/// running with <c>AUTH_ENFORCE=true</c>; there are no mocks and no in-process fakes. Each test owns
/// the sandbox it creates and deletes it in a <c>finally</c>, so the app-scoped session list stays
/// clean and the matrix is re-runnable.
/// </summary>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxLiveContractTests
{
    private readonly LiveGatewayFixture _fixture;

    public SandboxLiveContractTests(LiveGatewayFixture fixture) => _fixture = fixture;

    private SandboxClient Client
    {
        get
        {
            Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
            return _fixture.Client;
        }
    }

    private async Task<string> CreateSandboxAsync()
    {
        // The pinned gateway requires a non-empty workspace identifier; use a unique one per sandbox
        // so concurrently-running tests never share a workspace directory.
        var workspace = "wi187-" + Guid.NewGuid().ToString("N");
        var info = await Client.CreateAsync(new SandboxCreateRequest(workspace: workspace));
        info.SessionId.Should().NotBeNullOrWhiteSpace();
        return info.SessionId;
    }

    /// <summary>
    /// Builds a live-gateway client with a caller-chosen execution timeout, from the same environment
    /// credentials the fixture uses. The fixture's own <see cref="LiveGatewayFixture.Client"/> is fixed
    /// at 60s, which is too long to assert a timeout against; this lets a test drive a short window while
    /// still owning the app-scoped session the fixture client created.
    /// </summary>
    private SandboxClient CreateClientWithExecutionTimeout(TimeSpan executionTimeout)
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        var serverAddress = new Uri(Environment.GetEnvironmentVariable("SANDBOX_BASE_URL")!.Trim());
        var appId = Environment.GetEnvironmentVariable("SANDBOX_APP_ID")!.Trim();
        var appKey = Environment.GetEnvironmentVariable("SANDBOX_APP_KEY")!.Trim();
        var options = new SandboxClientOptions(
            serverAddress,
            appId,
            appKey,
            executionTimeout: executionTimeout,
            transportTimeout: TimeSpan.FromSeconds(30),
            allowInsecureDevelopmentTransport: string.Equals(serverAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        );
        return new SandboxClient(options);
    }

    [SkippableFact]
    public async Task Lifecycle_Create_Get_List_Delete_RoundTrips()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var fetched = await Client.GetAsync(sessionId);
            fetched.SessionId.Should().Be(sessionId);

            var listed = await Client.ListAsync();
            listed.Select(s => s.SessionId).Should().Contain(sessionId);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }

        // After deletion the session is gone: a foreign/missing session is a uniform NotFound.
        var afterDelete = await CaptureAsync(() => Client.GetAsync(sessionId));
        afterDelete.Should().NotBeNull();
        afterDelete!.Kind.Should().Be(SandboxErrorKind.NotFound);
    }

    [SkippableFact]
    public async Task Execute_ReturnsExactStdout_AndZeroExit()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var result = await Client.ExecuteAsync(
                sessionId,
                new SandboxCommand(["echo", "-n", "hello-exact"])
            );

            result.ExitCode.Should().Be(0);
            result.StandardOutput.Should().Be("hello-exact");
            result.OperationId.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task Execute_NonZeroExit_CapturesStderr()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var result = await Client.ExecuteAsync(
                sessionId,
                new SandboxCommand(["sh", "-c", "echo boom 1>&2; exit 7"])
            );

            result.ExitCode.Should().Be(7);
            result.StandardError.Should().Contain("boom");
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task Execute_SameOperationId_IsNotReRun()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var opId = Guid.NewGuid().ToString("N");
            var marker = "marker-" + opId;

            // A side-effecting append: if the op were re-run, the file would contain two lines.
            var command = new SandboxCommand(
                ["sh", "-c", $"echo {marker} >> /workspace/opid-probe.txt; echo done"],
                operationId: opId
            );

            var first = await Client.ExecuteAsync(sessionId, command);
            first.ExitCode.Should().Be(0);

            var second = await Client.ExecuteAsync(sessionId, command);
            second.ExitCode.Should().Be(0);
            second.OperationId.Should().Be(first.OperationId);

            var content = await Client.ReadTextFileAsync(sessionId, "opid-probe.txt");
            content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Should().ContainSingle(line => line == marker);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task Execute_ExceedingExecutionTimeout_IsExecutionTimeout()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            // A dedicated client with a deliberately short execution timeout so the gateway kills the
            // operation server-side (status timed_out) — or the SDK's own poll deadline elapses first —
            // well before the 30s sleep would finish. Both paths map to ExecutionTimeout, and the short
            // window keeps the assertion fast without a minute-long wait.
            using var shortTimeoutClient = CreateClientWithExecutionTimeout(TimeSpan.FromSeconds(5));

            var captured = await CaptureAsync(() =>
                shortTimeoutClient.ExecuteAsync(sessionId, new SandboxCommand(["sh", "-c", "sleep 30"]))
            );

            captured.Should().NotBeNull();
            captured!.Kind.Should().Be(SandboxErrorKind.ExecutionTimeout);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task Execute_LargeOutput_IsCapturedExactly_WithoutTruncation()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            // The operations API replaced the old MCP `Bash` channel, which silently truncated output at
            // 20 KB AND 500 lines. This payload crosses BOTH thresholds — 600 lines of 49 bytes + newline
            // = 30,000 bytes — so an exact-length, exact-line-count match proves output is captured
            // verbatim (byte-exact, untruncated), not merely "large enough not to notice".
            const int lineCount = 600;
            const int lineWidth = 49;
            var line = new string('A', lineWidth);
            var expectedBytes = lineCount * (lineWidth + 1); // + the newline `head` emits per line

            var result = await Client.ExecuteAsync(
                sessionId,
                new SandboxCommand(["sh", "-c", $"yes {line} | head -n {lineCount}"])
            );

            result.ExitCode.Should().Be(0);
            result.StandardOutput.Length.Should().Be(expectedBytes);
            result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(lineCount);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    /// <summary>
    /// The one acceptance criterion issue #464 exists for: a long-lived sandbox can reclaim a finished
    /// command's on-disk artifacts WITHOUT deleting itself. Executes a command, proves its artifacts are
    /// really on disk (reading the stdout file back through the files API — otherwise "they are gone
    /// afterwards" would prove nothing), deletes the operation, and then PINS the refusal the gateway
    /// actually gives for the deleted operation against the refusal it gives for one that never existed.
    /// ADR 0031 §6 claims those are uniform; this asserts the claim on the real gateway rather than
    /// assuming it.
    /// </summary>
    [SkippableFact]
    public async Task Operation_Delete_ReclaimsArtifacts_AndRefusesExactlyLikeANeverExistentOperation()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var opId = Guid.NewGuid().ToString("N");
            var result = await Client.ExecuteAsync(
                sessionId,
                new SandboxCommand(["echo", "-n", "artifact-probe"], operationId: opId)
            );
            result.ExitCode.Should().Be(0);
            result.StandardOutput.Should().Be("artifact-probe");

            // Locate the real artifact file. The reserved prefix is gateway-owned bookkeeping that is
            // readable (only WRITES below it are refused), so the SDK's ordinary listing walks it. The
            // generation directory name is chosen by the gateway, so it is read rather than guessed.
            var operations = await Client.ListDirectoryAsync(sessionId, ".mcp-gateway/operations");
            operations.Should().Contain(opId, "the executed operation must have left an artifact directory");
            var generation = (await Client.ListDirectoryAsync(sessionId, $".mcp-gateway/operations/{opId}"))
                .Should().ContainSingle("a single execution produces a single generation").Subject;
            var stdoutPath = $".mcp-gateway/operations/{opId}/{generation}/stdout";

            // Non-vacuity: the artifact is genuinely there and genuinely this command's output BEFORE the
            // delete, so every "it is gone" assertion below is about the delete and not about a path that
            // was never right.
            (await Client.ReadTextFileAsync(sessionId, stdoutPath)).Should().Be("artifact-probe");

            await Client.DeleteOperationAsync(sessionId, opId);

            // The footprint is actually reclaimed — the whole point of the call. The gateway's cleanup is
            // GENERATION-scoped (it removes `.mcp-gateway/operations/<id>/<generation>/`, which is what
            // closes the ABA hazard of a delayed delete reaping a re-reservation's artifacts), so the empty
            // `<id>` directory itself SURVIVES. That residue is asserted rather than ignored: the bytes are
            // gone, one empty directory per deleted operation is not, and a reader of this test should see
            // which of the two this call actually promises.
            (await Client.ListDirectoryAsync(sessionId, $".mcp-gateway/operations/{opId}"))
                .Should().BeEmpty("the generation directory holding the artifacts must be gone");
            (await Client.ListDirectoryAsync(sessionId, ".mcp-gateway/operations"))
                .Should().Contain(opId, "cleanup is generation-scoped: the empty per-operation directory is left behind");

            // PIN the artifact-GET refusal, and pin it BY COMPARISON: a path under a never-executed
            // operation id is the "never existed" control. Whatever shape the gateway chooses, the deleted
            // operation's artifact must answer identically — if it ever stopped doing so, the difference
            // would be an existence oracle for another app's operation ids.
            var neverExistentOpId = Guid.NewGuid().ToString("N");
            var deletedArtifact = await CaptureAsync(() => Client.ReadTextFileAsync(sessionId, stdoutPath));
            var neverExistentArtifact = await CaptureAsync(() =>
                Client.ReadTextFileAsync(sessionId, $".mcp-gateway/operations/{neverExistentOpId}/{generation}/stdout")
            );

            deletedArtifact.Should().NotBeNull("the artifact file must be gone after the operation is deleted");
            neverExistentArtifact.Should().NotBeNull();
            deletedArtifact!.Kind.Should().Be(SandboxErrorKind.NotFound);
            deletedArtifact.Kind.Should().Be(neverExistentArtifact!.Kind);
            deletedArtifact.ErrorCode.Should().Be(neverExistentArtifact.ErrorCode);

            // Same pinning for the operation RECORD, through the delete route itself (the SDK's poll is
            // internal to ExecuteAsync, so DELETE is the reachable probe of record existence). A second
            // delete of the same id must be indistinguishable from a delete of an id that never existed.
            var secondDelete = await CaptureAsync(() => Client.DeleteOperationAsync(sessionId, opId));
            var neverExistentDelete = await CaptureAsync(() => Client.DeleteOperationAsync(sessionId, neverExistentOpId));

            secondDelete.Should().NotBeNull("deleting an already-deleted operation is not silently a no-op");
            neverExistentDelete.Should().NotBeNull();
            secondDelete!.Kind.Should().Be(SandboxErrorKind.NotFound);
            secondDelete.Kind.Should().Be(neverExistentDelete!.Kind);
            secondDelete.ErrorCode.Should().Be(neverExistentDelete.ErrorCode);
            secondDelete.OperationId.Should().Be(opId);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    /// <summary>
    /// ADR 0031 §5 puts cancellation out of scope: deleting a STILL-RUNNING operation is refused with
    /// <c>409 operation_running</c>. Held open by submitting a long-running command through a task that is
    /// deliberately not awaited, then attempting the delete while it runs.
    /// </summary>
    [SkippableFact]
    public async Task Operation_DeleteWhileRunning_IsRefusedAsOperationRunningConflict()
    {
        var sessionId = await CreateSandboxAsync();
        var opId = Guid.NewGuid().ToString("N");
        // Long enough that the delete lands well inside the run, short enough that the awaited completion
        // below is not a meaningful cost even when the delete is refused on the first attempt.
        var running = Client.ExecuteAsync(sessionId, new SandboxCommand(["sh", "-c", "sleep 20"], operationId: opId));
        try
        {
            // Poll for the refusal rather than sleeping a fixed amount: before the gateway has reserved the
            // record the delete is a 404, which is a "not yet", not the answer under test. The loop FAILS
            // LOUDLY if it never observes the conflict — a silent give-up would make every assertion below
            // vacuous.
            SandboxException? refusal = null;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var captured = await CaptureAsync(() => Client.DeleteOperationAsync(sessionId, opId));
                captured.Should()
                    .NotBeNull("a RUNNING operation must never be deleted successfully — deletion is not cancellation");
                if (captured!.Kind == SandboxErrorKind.Conflict)
                {
                    refusal = captured;
                    break;
                }

                // The only other tolerable answer is "no such record yet" — the submit is still in flight.
                captured.Kind.Should().Be(SandboxErrorKind.NotFound);
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            refusal.Should().NotBeNull("the gateway must refuse the delete while the operation is running");
            refusal!.ErrorCode.Should().Be("operation_running");
            refusal.StatusCode.Should().Be(409);
            refusal.OperationId.Should().Be(opId);

            // Once terminal, the SAME delete succeeds — the refusal was about the operation's state, not a
            // permanent bar, which is what makes "wait, then delete" a real cleanup strategy.
            (await running).ExitCode.Should().Be(0);
            await Client.DeleteOperationAsync(sessionId, opId);
        }
        finally
        {
            // Never leave the execute task unobserved, even when an assertion above threw.
            try
            {
                _ = await running;
            }
            catch (SandboxException)
            {
                // The command's own outcome is asserted above; here we only drain the task.
            }

            await Client.DeleteAsync(sessionId);
        }
    }

    /// <summary>
    /// ADR 0031 §2: a write under the reserved <c>.mcp-gateway/operations</c> prefix is rejected by the
    /// gateway before any I/O. The prefix is gateway-owned bookkeeping — readable (the delete test above
    /// lists it), never writable — so this is a refusal a <c>WriteTextFileAsync</c> caller can actually hit.
    /// </summary>
    [SkippableFact]
    public async Task Write_UnderTheReservedOperationsPrefix_IsRefused()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var captured = await CaptureAsync(() =>
                Client.WriteTextFileAsync(sessionId, ".mcp-gateway/operations/intruder.txt", "nope")
            );

            captured.Should().NotBeNull("the reserved prefix must refuse writes");
            captured!.Kind.Should().Be(SandboxErrorKind.Authorization);

            // The same content at an ordinary path is accepted, so the refusal is about the PREFIX and not
            // about the write path being broken.
            await Client.WriteTextFileAsync(sessionId, "intruder.txt", "nope");
            (await Client.ReadTextFileAsync(sessionId, "intruder.txt")).Should().Be("nope");
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task File_WriteThenRead_RoundTripsExactUtf8()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            const string path = "nested/dir/greeting.txt";
            var content = "héllo\nwörld\t— 日本語 🌐\nlast line no newline";

            await Client.WriteTextFileAsync(sessionId, path, content);
            var readBack = await Client.ReadTextFileAsync(sessionId, path);

            readBack.Should().Be(content);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ListDirectory_IncludesDotfilesAndSpacedNames()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            await Client.WriteTextFileAsync(sessionId, "listing/plain.txt", "a");
            await Client.WriteTextFileAsync(sessionId, "listing/a b.txt", "b");
            await Client.WriteTextFileAsync(sessionId, "listing/.hidden", "c");

            var names = await Client.ListDirectoryAsync(sessionId, "listing");

            names.Should().Contain(["plain.txt", "a b.txt", ".hidden"]);
            names.Should().NotContain([".", ".."]);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task PreviewMarketplaces_DoesNotThrow()
    {
        _ = Client; // gate on availability
        var catalog = await Client.PreviewMarketplacesAsync();
        catalog.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task ListDiscovered_ReturnsAList()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            var discovered = await Client.ListDiscoveredAsync(sessionId);
            discovered.Should().NotBeNull();
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ForeignOrMissingSession_Get_IsNotFound()
    {
        var captured = await CaptureAsync(() => Client.GetAsync(Guid.NewGuid().ToString()));
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(SandboxErrorKind.NotFound);
    }

    [SkippableFact]
    public async Task WrongCredential_IsAuthorization()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

        var serverAddress = new Uri(Environment.GetEnvironmentVariable("SANDBOX_BASE_URL")!.Trim());
        var appId = Environment.GetEnvironmentVariable("SANDBOX_APP_ID")!.Trim();

        // A well-formed but WRONG secret (valid standard base64, >=32 bytes) — the SDK accepts it at
        // construction; the gateway rejects it as 401 -> Authorization.
        var wrongKey = Convert.ToBase64String(new byte[32]);
        var options = new SandboxClientOptions(
            serverAddress,
            appId,
            wrongKey,
            executionTimeout: TimeSpan.FromSeconds(30),
            transportTimeout: TimeSpan.FromSeconds(15),
            allowInsecureDevelopmentTransport: string.Equals(serverAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        );

        using var wrongClient = new SandboxClient(options);
        var captured = await CaptureAsync(() => wrongClient.ListAsync());
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(SandboxErrorKind.Authorization);
    }

    private static async Task<SandboxException?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (SandboxException ex)
        {
            return ex;
        }
    }
}
