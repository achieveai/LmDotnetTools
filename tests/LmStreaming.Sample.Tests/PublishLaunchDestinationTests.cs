using System.Security.Cryptography;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// Real-filesystem coverage for the <c>-DestinationDirectory</c> atomic sibling-swap publish
/// extension (see docs/superpowers/specs/2026-08-10-lmstreaming-standalone-publish-design.md,
/// "Extension: -DestinationDirectory"). Every test dot-sources the real <c>publish-launch.ps1</c>
/// via <see cref="PublishLaunchScriptHost"/> and calls its internal helper functions directly
/// against fixture directories built on disk in this test's own temp root -- no <c>npm</c> and no
/// <c>dotnet publish</c> ever runs: a "staged" directory is hand-built to look exactly like a
/// validated publish output, standing in for what <c>Invoke-DestinationPublish</c> would have
/// produced from a real build.
/// </summary>
public sealed class PublishLaunchDestinationTests : IDisposable
{
    private readonly string _root = Directory
        .CreateDirectory(Path.Combine(Path.GetTempPath(), "pldest-" + Guid.NewGuid().ToString("N")))
        .FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a lingering handle from a spawned pwsh process must never fail the test run.
        }
    }

    // ----------------------------------------------------------------------------------------
    // Fixture builders
    // ----------------------------------------------------------------------------------------

    private static string CreateStagedDirectory(string parent, string assetToken, string? decoyEnvContent = null)
    {
        var staged = Path.Combine(parent, "staged");
        var assetsDir = Path.Combine(staged, "wwwroot", "dist", "assets");
        Directory.CreateDirectory(assetsDir);

        File.WriteAllText(Path.Combine(staged, "LmStreaming.Sample.exe"), "staged-exe-" + assetToken);
        File.WriteAllText(Path.Combine(staged, "appsettings.json"), "{\"source\":\"staged\"}");

        var assetFileName = $"app.{assetToken}.js";
        File.WriteAllText(Path.Combine(assetsDir, assetFileName), $"console.log('{assetToken}');");
        File.WriteAllText(
            Path.Combine(staged, "wwwroot", "dist", "index.html"),
            $"<html><body><script src=\"/dist/assets/{assetFileName}\"></script></body></html>"
        );

        if (decoyEnvContent is not null)
        {
            File.WriteAllText(Path.Combine(staged, ".env"), decoyEnvContent);
        }

        return staged;
    }

    private static string CreateRecognizedDestination(
        string parent,
        string name,
        string oldAssetToken,
        string envContent = "REAL_DESTINATION_ENV=1",
        bool seedNotifyWaitsDb = true
    )
    {
        var destination = Path.Combine(parent, name);
        var assetsDir = Path.Combine(destination, "wwwroot", "dist", "assets");
        Directory.CreateDirectory(assetsDir);

        File.WriteAllText(Path.Combine(destination, "LmStreaming.Sample.exe"), "existing-exe");
        File.WriteAllText(Path.Combine(destination, "appsettings.json"), "{\"existing\":true}");

        var oldAssetFileName = $"app.{oldAssetToken}.js";
        File.WriteAllText(Path.Combine(assetsDir, oldAssetFileName), "console.log('old');");
        File.WriteAllText(
            Path.Combine(destination, "wwwroot", "dist", "index.html"),
            $"<html><body><script src=\"/dist/assets/{oldAssetFileName}\"></script></body></html>"
        );

        WriteFile(destination, "conversations", "thread-1.json", "{\"id\":\"thread-1\"}");
        WriteFile(destination, "oauth-tokens", "token.json", "secret-token-value");
        WriteFile(destination, "workspaces", "demo.json", "{\"workspace\":\"demo\"}");
        WriteFile(destination, "chat-modes", "default.json", "{\"mode\":\"default\"}");
        WriteFile(destination, "workflow-index", "index.json", "{}");
        WriteFile(destination, "logs", "app.log", "log line 1\nlog line 2\n");
        WriteFile(destination, "recordings", "session.jsonl", "{\"event\":1}\n");
        File.WriteAllText(Path.Combine(destination, ".env"), envContent);

        if (seedNotifyWaitsDb)
        {
            SeedNotifyWaitsDatabaseAsync(Path.Combine(destination, "notify-waits.db")).GetAwaiter().GetResult();
        }

        return destination;
    }

    private static void WriteFile(string destinationRoot, string subdirectory, string fileName, string content)
    {
        var dir = Path.Combine(destinationRoot, subdirectory);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    private static async Task SeedNotifyWaitsDatabaseAsync(string databasePath)
    {
        await using (var factory = new SqliteConnectionFactory(databasePath))
        {
            var store = new SqliteNotifyWaitStore(factory);
            await store.SaveAsync(
                new NotifyWaitRecord(
                    WaitId: "wait-1",
                    ThreadId: "thread-1",
                    Kind: "notify",
                    Args: "{}",
                    Label: "preserve-test",
                    MaxFires: 1,
                    FiresSoFar: 0,
                    TimeoutAtUnixMs: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
                    ArmedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    // Lowercase to match production's convention (TriggerRuntime.cs writes "active") and
                    // SqliteNotifyWaitStore.LoadActiveAsync's `status = 'active'` filter -- SQLite's default
                    // BINARY collation is case-sensitive, so a capitalized value here would silently never
                    // match the read-back query regardless of whether the preserve-copy itself is correct.
                    Status: "active"
                )
            );

            // Force a WAL checkpoint so the sidecars reflect a coherent, fully-committed snapshot --
            // standing in for the "app stopped" state Checkpoint B relies on before this file and its
            // sidecars are copied byte-for-byte.
            await using var connection = await factory.GetConnectionAsync();
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        // Belt-and-suspenders even after the SqliteConnectionFactory.PooledConnection.DisposeAsync
        // fix (which was the real bug: it no longer skips its own native-close call): clearing all
        // pools guarantees every native handle this seeding step opened is genuinely closed before
        // the pwsh child spawned moments later attempts to rename the whole destination directory.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static string CreateEmptyDestination(string parent, string name)
    {
        var destination = Path.Combine(parent, name);
        Directory.CreateDirectory(destination);
        return destination;
    }

    private static string CreateUnrecognizedDestination(
        string parent,
        string name,
        bool hasExe,
        bool hasAppsettings,
        bool hasIndex
    )
    {
        var destination = Path.Combine(parent, name);
        Directory.CreateDirectory(destination);

        if (hasExe)
        {
            File.WriteAllText(Path.Combine(destination, "LmStreaming.Sample.exe"), "not-a-real-exe");
        }

        if (hasAppsettings)
        {
            File.WriteAllText(Path.Combine(destination, "appsettings.json"), "{}");
        }

        if (hasIndex)
        {
            var distDir = Path.Combine(destination, "wwwroot", "dist");
            Directory.CreateDirectory(distDir);
            File.WriteAllText(Path.Combine(distDir, "index.html"), "<html></html>");
        }

        if (!hasExe && !hasAppsettings && !hasIndex)
        {
            File.WriteAllText(Path.Combine(destination, "some-foreign-file.txt"), "not ours");
        }

        return destination;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void AssertByteIdentical(string expectedPath, string actualPath, string because)
    {
        File.Exists(expectedPath).Should().BeTrue($"expected file should exist: {expectedPath}");
        File.Exists(actualPath).Should().BeTrue($"actual file should exist: {actualPath} ({because})");
        Hash(actualPath).Should().Be(Hash(expectedPath), because);
    }

    private static string? FindSiblingWithSuffix(string parent, string destinationName, string suffix) =>
        Directory
            .GetDirectories(parent)
            .FirstOrDefault(d =>
                Path.GetFileName(d).StartsWith($"{destinationName}.{suffix}", StringComparison.Ordinal)
            );

    private static string ProcessDelegateThatThrowsIfInvoked =>
        "{ param() throw 'TEST FAILURE: process enumeration must not be invoked for this destination state' }";

    private static string ProcessDelegateReportingRunningOnCall(int callNumber, string executablePath)
    {
        var quotedPath = PublishLaunchScriptHost.QuoteSingle(executablePath);
        return "{ param() "
            + "if (-not (Get-Variable -Name __pldCallCount -Scope Script -ErrorAction SilentlyContinue)) { $script:__pldCallCount = 0 }; "
            + "$script:__pldCallCount++; "
            + $"if ($script:__pldCallCount -eq {callNumber}) {{ return @([pscustomobject]@{{ Id = 4321; Path = '{quotedPath}' }}) }}; "
            + "return @() }";
    }

    private static string MoveDelegateThatFailsOnCall(int callNumber) => MoveDelegateThatFailsOnCalls(callNumber);

    /// <summary>
    /// A move delegate that throws on each of <paramref name="callNumbers"/> (1-based) and performs
    /// a real rename otherwise. Multiple numbers exist so a test can fail the swap AND the rollback
    /// that follows it -- calls 2 and 3 -- which is the only way to reach the double-failure branch.
    /// </summary>
    private static string MoveDelegateThatFailsOnCalls(params int[] callNumbers)
    {
        var condition = string.Join(" -or ", callNumbers.Select(n => $"$script:__pldMoveCallCount -eq {n}"));
        return "{ param($From, $To) "
            + "if (-not (Get-Variable -Name __pldMoveCallCount -Scope Script -ErrorAction SilentlyContinue)) { $script:__pldMoveCallCount = 0 }; "
            + "$script:__pldMoveCallCount++; "
            + $"if ({condition}) {{ throw ('Simulated move failure on call ' + $script:__pldMoveCallCount) }}; "
            + "[System.IO.Directory]::Move($From, $To) }";
    }

    /// <summary>
    /// A move delegate that fails its first <paramref name="transientFailureCount"/> invocations
    /// with a REAL, wrapped <see cref="System.IO.IOException"/> -- deterministically, via a static
    /// <c>[System.IO.Directory]::Move</c> call whose target already exists, never via a real
    /// filesystem race -- before performing the actual requested move. This is what #371's retry in
    /// <c>Invoke-AtomicMove</c> is keyed on: PowerShell wraps a failing static-method call in a
    /// <c>MethodInvocationException</c> whose <c>InnerException</c> is the real CLR exception type
    /// (here, exactly <c>IOException</c>). That is unlike every <c>MoveDelegateThatFails*</c> helper
    /// above, whose bare PowerShell <c>throw</c> produces a <c>RuntimeException</c> with no inner
    /// exception at all and so is never retried -- see
    /// <see cref="Deploy_Recognized_RollbackOnSecondMoveFailure_DestinationByteIdenticalAndCandidateRetained"/>
    /// immediately below, which still fails on the first (and only) attempt with this same
    /// production code, proving the two failure shapes are told apart.
    /// <paramref name="markerFrom"/>/<paramref name="markerExisting"/> are throwaway fixture
    /// directories dedicated to producing the failure; <paramref name="markerExisting"/> is never
    /// actually replaced because the move against it always throws before touching the filesystem.
    /// </summary>
    private static string MoveDelegateThatFailsTransientlyThenSucceeds(
        int transientFailureCount,
        string markerFrom,
        string markerExisting
    )
    {
        var quotedMarkerFrom = PublishLaunchScriptHost.QuoteSingle(markerFrom);
        var quotedMarkerExisting = PublishLaunchScriptHost.QuoteSingle(markerExisting);
        return "{ param($From, $To) "
            + "if (-not (Get-Variable -Name __pldTransientCallCount -Scope Script -ErrorAction SilentlyContinue)) { $script:__pldTransientCallCount = 0 }; "
            + "$script:__pldTransientCallCount++; "
            + $"if ($script:__pldTransientCallCount -le {transientFailureCount}) {{ [System.IO.Directory]::Move('{quotedMarkerFrom}', '{quotedMarkerExisting}') }}; "
            + "[System.IO.Directory]::Move($From, $To) }";
    }

    /// <summary>
    /// A move delegate that: succeeds on call 1 (rename 1, destination -&gt; backup); fails call 2
    /// (rename 2, candidate -&gt; destination) with a bare, non-transient throw, so the swap enters
    /// <c>Invoke-CandidateSwap</c>'s rollback branch; then fails the ROLLBACK move
    /// (backup -&gt; destination, calls 3 onward) with <paramref name="rollbackTransientFailureCount"/>
    /// real, wrapped <see cref="System.IO.IOException"/>s -- the same #371 failure shape as
    /// <see cref="MoveDelegateThatFailsTransientlyThenSucceeds"/> -- before letting it succeed.
    /// Targets the rollback move specifically: issue #371's actual flake was on the rollback rename
    /// (call 3, inside <c>Invoke-CandidateSwap</c>'s guarded catch block), not the first rename that
    /// <see cref="MoveDelegateThatFailsTransientlyThenSucceeds"/> exercises.
    /// </summary>
    private static string MoveDelegateThatFailsSwapThenRecoversRollbackViaRetry(
        int rollbackTransientFailureCount,
        string markerFrom,
        string markerExisting
    )
    {
        var quotedMarkerFrom = PublishLaunchScriptHost.QuoteSingle(markerFrom);
        var quotedMarkerExisting = PublishLaunchScriptHost.QuoteSingle(markerExisting);
        var lastTransientCall = 2 + rollbackTransientFailureCount;
        return "{ param($From, $To) "
            + "if (-not (Get-Variable -Name __pldSwapRollbackCallCount -Scope Script -ErrorAction SilentlyContinue)) { $script:__pldSwapRollbackCallCount = 0 }; "
            + "$script:__pldSwapRollbackCallCount++; "
            + "if ($script:__pldSwapRollbackCallCount -eq 2) { throw ('Simulated non-transient swap failure on call ' + $script:__pldSwapRollbackCallCount) }; "
            + $"if ($script:__pldSwapRollbackCallCount -ge 3 -and $script:__pldSwapRollbackCallCount -le {lastTransientCall}) {{ [System.IO.Directory]::Move('{quotedMarkerFrom}', '{quotedMarkerExisting}') }}; "
            + "[System.IO.Directory]::Move($From, $To) }";
    }

    /// <summary>A probe delegate that always reports the executable as free (nothing holds it).</summary>
    private const string ProbeDelegateReportingNotHeld = "{ param($Path) return $false }";

    /// <summary>A probe delegate that always reports the executable as held (locked / indeterminate).</summary>
    private const string ProbeDelegateReportingHeld = "{ param($Path) return $true }";

    /// <summary>
    /// A process-enumeration delegate whose single entry does not reveal its <c>.Path</c> -- exactly
    /// what <c>Get-Process</c> produces for a process running elevated or as another user.
    /// <para>
    /// The path is <c>$null</c>, NOT a throwing property, because that is what actually happens:
    /// PowerShell's <c>Path</c> on a Process is an ETS ScriptProperty over
    /// <c>MainModule.FileName</c>, and it swallows the underlying Win32Exception and yields
    /// <c>$null</c>. Verified against a live <c>Get-Process</c> on this machine, where dozens of
    /// ordinary processes report a null Path. A throwing test double would have exercised a
    /// production branch that never fires in reality.
    /// </para>
    /// <paramref name="processName"/> is still readable, because <c>ProcessName</c> needs no privilege.
    /// </summary>
    private static string ProcessDelegateWithUnreadablePath(string processName) =>
        "{ param() return @([pscustomobject]@{ Id = 9876; ProcessName = '"
        + PublishLaunchScriptHost.QuoteSingle(processName)
        + "'; Path = $null }) }";

    // ----------------------------------------------------------------------------------------
    // Destination classification (Test-DestinationState)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void DestinationState_Missing_WhenPathDoesNotExist()
    {
        var missingPath = Path.Combine(_root, "does-not-exist");

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(missingPath)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        json.RootElement.GetProperty("State").GetString().Should().Be("Missing");
    }

    [Fact]
    public void DestinationState_Empty_WhenDirectoryHasZeroEntries()
    {
        var empty = CreateEmptyDestination(_root, "empty-dest");

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(empty)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        json.RootElement.GetProperty("State").GetString().Should().Be("Empty");
    }

    [Fact]
    public void DestinationState_NonEmptyWithOnlyAHiddenFile_IsNotClassifiedEmpty()
    {
        var destination = CreateEmptyDestination(_root, "hidden-only-dest");
        var hiddenFile = Path.Combine(destination, "desktop.ini");
        File.WriteAllText(hiddenFile, "[.ShellClassInfo]");
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        json.RootElement.GetProperty("State")
            .GetString()
            .Should()
            .Be(
                "Unrecognized",
                "a hidden entry still counts as an entry -- the directory is not empty and has no recognition markers"
            );
    }

    [Fact]
    public void DestinationState_Recognized_WhenAllThreeMarkersPresent()
    {
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "abc111");

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        json.RootElement.GetProperty("State").GetString().Should().Be("Recognized");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void DestinationState_Unrecognized_WhenMarkersAreAbsentOrOnlyTwoOfThree(
        bool hasExe,
        bool hasAppsettings,
        bool hasIndex
    )
    {
        var destination = CreateUnrecognizedDestination(
            _root,
            $"unrecognized-{hasExe}-{hasAppsettings}-{hasIndex}",
            hasExe,
            hasAppsettings,
            hasIndex
        );

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        json.RootElement.GetProperty("State").GetString().Should().Be("Unrecognized");
    }

    // ----------------------------------------------------------------------------------------
    // Unrecognized -> fail before any build/candidate work
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Deploy_Unrecognized_FailsBeforeAnyCandidateCreated_DestinationUntouched()
    {
        var staged = CreateStagedDirectory(_root, "fresh1");
        var destination = CreateUnrecognizedDestination(
            _root,
            "unrecognized-dest",
            hasExe: true,
            hasAppsettings: true,
            hasIndex: false
        );
        var sentinelBefore = File.ReadAllText(Path.Combine(destination, "LmStreaming.Sample.exe"));
        var entriesBefore = Directory.GetFileSystemEntries(destination).Length;

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}"
        );

        result.Succeeded.Should().BeFalse("an Unrecognized non-empty destination must be rejected");
        result.StandardError.Should().Contain("Unrecognized");
        File.ReadAllText(Path.Combine(destination, "LmStreaming.Sample.exe"))
            .Should()
            .Be(sentinelBefore, "the destination must be untouched");
        Directory
            .GetFileSystemEntries(destination)
            .Length.Should()
            .Be(entriesBefore, "no candidate/backup work should have started");
        FindSiblingWithSuffix(_root, "unrecognized-dest", "candidate-")
            .Should()
            .BeNull("no candidate sibling may ever be created for an Unrecognized destination");
    }

    // ----------------------------------------------------------------------------------------
    // Missing / Empty deploy
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Deploy_Missing_CreatesDestination_ByteIdenticalToStaged_ViaSingleRename()
    {
        var staged = CreateStagedDirectory(_root, "missing1");
        var destination = Path.Combine(_root, "missing-dest");

        var expectedExeHash = Hash(Path.Combine(staged, "LmStreaming.Sample.exe"));
        var expectedAssetHash = Hash(Path.Combine(staged, "wwwroot", "dist", "assets", "app.missing1.js"));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        Directory.Exists(destination).Should().BeTrue();
        File.Exists(Path.Combine(destination, "LmStreaming.Sample.exe"))
            .Should()
            .BeTrue("the Missing-destination deploy must copy the staged output byte-for-byte");
        Hash(Path.Combine(destination, "LmStreaming.Sample.exe"))
            .Should()
            .Be(expectedExeHash, "the Missing-destination deploy must copy the staged output byte-for-byte");
        File.Exists(Path.Combine(destination, "wwwroot", "dist", "assets", "app.missing1.js"))
            .Should()
            .BeTrue("the staged asset must survive the swap unchanged");
        Hash(Path.Combine(destination, "wwwroot", "dist", "assets", "app.missing1.js"))
            .Should()
            .Be(expectedAssetHash, "the staged asset must survive the swap unchanged");

        // The single move is FROM A CANDIDATE ASSEMBLED AS A SAME-PARENT SIBLING OF THE
        // DESTINATION, not from $StagedDirectory directly: $StagedDirectory can live on a
        // different volume than the destination (e.g. the repo's own scratchpad tree vs. an
        // external -DestinationDirectory), and Directory.Move cannot cross volumes. Renaming
        // staged itself would also consume it, unlike every other branch (Empty/Recognized both
        // leave $StagedDirectory intact for the caller). So staged must still exist afterwards,
        // and no leftover candidate/backup sibling should remain once the single rename succeeds.
        Directory
            .Exists(staged)
            .Should()
            .BeTrue(
                "the staged directory must be left intact by the deploy, exactly like the Empty and Recognized branches -- only a same-parent candidate assembled from it is ever moved"
            );
        FindSiblingWithSuffix(_root, "missing-dest", "candidate-")
            .Should()
            .BeNull("the candidate must have been renamed into place, leaving no leftover candidate sibling");
        FindSiblingWithSuffix(_root, "missing-dest", "backup-")
            .Should()
            .BeNull("there was nothing pre-existing to back up for a Missing destination");
    }

    [Fact]
    public void Deploy_Empty_RollbackOnSecondMoveFailure_DestinationByteIdenticalAndCandidateRetained()
    {
        // Mirrors Deploy_Recognized_RollbackOnSecondMoveFailure_...: the Empty branch performs the
        // exact same two-move shape (existing -> backup, then candidate -> destination) as
        // Recognized, so a failure on the second move must roll back identically -- restoring the
        // (empty) destination and retaining the assembled candidate for inspection, rather than
        // leaving the destination missing and the error unrecoverable.
        var staged = CreateStagedDirectory(_root, "emptyrollback1");
        var destination = CreateEmptyDestination(_root, "empty-rollback-dest");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-MoveDelegate {MoveDelegateThatFailsOnCall(2)}"
        );

        result
            .Succeeded.Should()
            .BeFalse("the second rename (candidate -> destination) was made to fail deterministically");
        result.StandardError.Should().Contain("rolled back");

        Directory.Exists(destination).Should().BeTrue("the destination must exist after rollback");
        Directory
            .GetFileSystemEntries(destination)
            .Should()
            .BeEmpty("the destination was empty before this run and must be byte-identical (empty) after rollback");

        FindSiblingWithSuffix(_root, "empty-rollback-dest", "backup-")
            .Should()
            .BeNull("the backup must have been renamed back onto the destination path, so no backup sibling remains");
        var candidate = FindSiblingWithSuffix(_root, "empty-rollback-dest", "candidate-");
        candidate
            .Should()
            .NotBeNull("the candidate must be retained on disk for inspection, not deleted, after a rollback");
        File.ReadAllText(Path.Combine(candidate!, "appsettings.json"))
            .Should()
            .Be(
                "{\"source\":\"staged\"}",
                "the retained candidate is the fully-assembled one from before the failed swap"
            );
    }

    [Fact]
    public void Deploy_Recognized_StagedNotifyWaitsSidecarNeverLeaksThrough_WhenExistingDestinationHasNoSidecar()
    {
        // The preserve list protects "notify-waits.db" by name, and Copy-PreserveSet correctly
        // carries over its -wal/-shm/-journal sidecars FROM THE EXISTING DESTINATION -- but
        // Copy-ReplaceSet (which copies everything else FROM STAGED, excluding only literal
        // preserve-list names) did not know about those sidecar names, so a stray sidecar file
        // present in a staged publish output could leak straight into the destination whenever the
        // existing destination has no sidecar of its own for Copy-PreserveSet to overwrite it with.
        var staged = CreateStagedDirectory(_root, "sidecarleak1");
        File.WriteAllText(
            Path.Combine(staged, "notify-waits.db-wal"),
            "STALE STAGED WAL -- must never leak into the destination"
        );

        // seedNotifyWaitsDb: false -- the existing destination has no notify-waits.db (and
        // therefore no sidecars) at all, so Copy-PreserveSet's own sidecar copy is a no-op here;
        // only Copy-ReplaceSet's exclusion can prevent the leak.
        var destination = CreateRecognizedDestination(_root, "sidecar-leak-dest", "oldSC", seedNotifyWaitsDb: false);

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        File.Exists(Path.Combine(destination, "notify-waits.db-wal"))
            .Should()
            .BeFalse(
                "a stray sidecar present in the staged output must never leak into the destination -- Copy-ReplaceSet must exclude notify-waits.db's sidecar names, not just the bare db filename"
            );
    }

    [Fact]
    public void Deploy_Empty_PerformsTwoRenameSwap_ByteIdenticalToStaged_BackupRemoved()
    {
        var staged = CreateStagedDirectory(_root, "empty1");
        var destination = CreateEmptyDestination(_root, "empty-dest");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        AssertByteIdentical(
            Path.Combine(staged, "LmStreaming.Sample.exe"),
            Path.Combine(destination, "LmStreaming.Sample.exe"),
            "the Empty-destination deploy must land the staged output byte-for-byte"
        );
        FindSiblingWithSuffix(_root, "empty-dest", "backup-")
            .Should()
            .BeNull("the backup (the old empty directory) must be removed after a successful swap");
    }

    // ----------------------------------------------------------------------------------------
    // Recognized-deployment preserve/replace semantics
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Deploy_Recognized_PreservesListedPathsByteForByte_IncludingSqliteSidecars_FunctionalReadThroughRealStore()
    {
        var staged = CreateStagedDirectory(_root, "rec1");
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "old1");

        // Snapshot hashes of every preserved path BEFORE the run.
        var preserveRelativeFiles = new[]
        {
            Path.Combine("conversations", "thread-1.json"),
            Path.Combine("oauth-tokens", "token.json"),
            Path.Combine("workspaces", "demo.json"),
            Path.Combine("chat-modes", "default.json"),
            Path.Combine("workflow-index", "index.json"),
            Path.Combine("logs", "app.log"),
            Path.Combine("recordings", "session.jsonl"),
            ".env",
            "notify-waits.db",
        };
        var hashesBefore = preserveRelativeFiles.ToDictionary(
            relative => relative,
            relative => Hash(Path.Combine(destination, relative))
        );

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);

        foreach (var relative in preserveRelativeFiles)
        {
            Hash(Path.Combine(destination, relative))
                .Should()
                .Be(hashesBefore[relative], $"preserved path '{relative}' must be byte-identical after the swap");
        }

        // Replace-set must have come from staging.
        File.ReadAllText(Path.Combine(destination, "appsettings.json")).Should().Be("{\"source\":\"staged\"}");
        File.ReadAllText(Path.Combine(destination, "LmStreaming.Sample.exe")).Should().StartWith("staged-exe-");

        // Functional coherence proof (not only a byte hash): open the post-swap notify-waits.db
        // through the real production store and confirm the previously-committed row is readable.
        var readBack = await ReadNotifyWaitsAsync(Path.Combine(destination, "notify-waits.db"));
        readBack
            .Should()
            .ContainSingle(r => r.WaitId == "wait-1" && r.ThreadId == "thread-1" && r.Label == "preserve-test");

        // The successful-Recognized path's backup cleanup was previously asserted ONLY for the Empty
        // branch. A retained backup here is not cosmetic: it is a full second copy of the previous
        // deployment, including oauth-tokens/ and .env, left unencrypted beside the live directory
        // and silently doubling disk use on every upgrade.
        FindSiblingWithSuffix(_root, "recognized-dest", "backup-")
            .Should()
            .BeNull(
                "the previous deployment's backup must be removed after a successful swap, not left beside the destination holding a second copy of .env and oauth-tokens/"
            );
        FindSiblingWithSuffix(_root, "recognized-dest", "candidate-")
            .Should()
            .BeNull("the candidate must have been renamed into place, leaving no leftover sibling");
    }

    [Fact]
    public void Deploy_Recognized_RollbackAlsoFails_ErrorNamesBothSiblingsAndBothCauses()
    {
        // The double-failure window: between the two renames the destination path does not exist at
        // all. If the rollback (call 3) fails too, the operator is left with NO destination and two
        // siblings -- and the previous deployment, including the only copy of .env and
        // oauth-tokens/, lives in one of them. A bare rollback call would surface the ROLLBACK's
        // exception and discard the original swap error, naming neither directory, so the operator
        // would have no way to know which sibling to rename back.
        var staged = CreateStagedDirectory(_root, "dblfail1");
        var destination = CreateRecognizedDestination(_root, "dblfail-dest", "oldDF");
        var envHashBefore = Hash(Path.Combine(destination, ".env"));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-MoveDelegate {MoveDelegateThatFailsOnCalls(2, 3)}"
        );

        result.Succeeded.Should().BeFalse("both the swap and its rollback were made to fail deterministically");

        var backup = FindSiblingWithSuffix(_root, "dblfail-dest", "backup-");
        var candidate = FindSiblingWithSuffix(_root, "dblfail-dest", "candidate-");
        backup
            .Should()
            .NotBeNull(
                "the previous deployment is still on disk under its backup name -- that is the whole recovery path"
            );
        candidate.Should().NotBeNull("the assembled candidate must be retained for inspection");
        Directory
            .Exists(destination)
            .Should()
            .BeFalse(
                "sanity: the destination genuinely does not exist in this window -- that is what makes naming the backup path load-bearing"
            );

        var stderr = result.StandardError;
        stderr
            .Should()
            .Contain(backup!, "the error must name the exact backup directory the operator has to rename back");
        stderr.Should().Contain(candidate!, "the error must name the retained candidate");
        stderr
            .Should()
            .Contain(
                "Swap error",
                "the ORIGINAL swap failure must survive -- not be replaced by the rollback's own exception"
            );
        stderr.Should().Contain("Rollback error", "the rollback failure must be reported too");

        Hash(Path.Combine(backup!, ".env"))
            .Should()
            .Be(
                envHashBefore,
                "the operator's only copy of .env must be intact inside the backup the error points them at"
            );
    }

    private static async Task<IReadOnlyList<NotifyWaitRecord>> ReadNotifyWaitsAsync(string databasePath)
    {
        await using var factory = new SqliteConnectionFactory(databasePath);
        var store = new SqliteNotifyWaitStore(factory);
        return await store.LoadActiveAsync("thread-1");
    }

    [Fact]
    public void Deploy_Recognized_EnvSourcedOnlyFromExistingDestination_DecoyStagedEnvNeverAppears()
    {
        var staged = CreateStagedDirectory(_root, "envtest1", decoyEnvContent: "DECOY_FROM_STAGING=1");
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "old2", envContent: "REAL_ENV=42");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        var finalEnv = File.ReadAllText(Path.Combine(destination, ".env"));
        finalEnv
            .Should()
            .Be("REAL_ENV=42", "the destination .env must be the pre-existing one, never the staged decoy");
        finalEnv.Should().NotContain("DECOY_FROM_STAGING");
    }

    [Fact]
    public void Deploy_Recognized_NoStaleAssets_OldHashedAssetAbsentAfterRebuild()
    {
        var staged = CreateStagedDirectory(_root, "newhash1");
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "oldhashXYZ");
        var oldAssetPath = Path.Combine(destination, "wwwroot", "dist", "assets", "app.oldhashXYZ.js");
        File.Exists(oldAssetPath).Should().BeTrue("sanity: the old hashed asset must exist before the run");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        File.Exists(oldAssetPath)
            .Should()
            .BeFalse(
                "the old hashed Vite asset must not survive -- the candidate's wwwroot/dist is built solely from the fresh staged output"
            );
        File.Exists(Path.Combine(destination, "wwwroot", "dist", "assets", "app.newhash1.js"))
            .Should()
            .BeTrue("the new hashed asset must be present");
    }

    // ----------------------------------------------------------------------------------------
    // Running-instance checkpoints (Recognized-deployment only)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Deploy_MissingAndEmpty_NeverInvokeProcessEnumeration()
    {
        var stagedForMissing = CreateStagedDirectory(_root, "chkm1");
        var missingDestination = Path.Combine(_root, "missing-nocheck-dest");
        var missingResult = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(stagedForMissing)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(missingDestination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}"
        );
        missingResult.Succeeded.Should().BeTrue(missingResult.StandardError);

        var stagedForEmpty = CreateStagedDirectory(_root, "chke1");
        var emptyDestination = CreateEmptyDestination(_root, "empty-nocheck-dest");
        var emptyResult = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(stagedForEmpty)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(emptyDestination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}"
        );
        emptyResult.Succeeded.Should().BeTrue(emptyResult.StandardError);
    }

    [Fact]
    public void Deploy_Recognized_CheckpointA_FailsWhenProcessRunning_BeforeCandidateCreated()
    {
        var staged = CreateStagedDirectory(_root, "chkA1");
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "oldA");
        var exePath = Path.Combine(destination, "LmStreaming.Sample.exe");
        var originalExeContent = File.ReadAllText(exePath);

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateReportingRunningOnCall(1, exePath)}"
        );

        result.Succeeded.Should().BeFalse("Checkpoint A must fail fast when the destination executable is running");
        result.StandardError.Should().Contain("Checkpoint A");
        File.ReadAllText(exePath).Should().Be(originalExeContent, "destination must be untouched");
        FindSiblingWithSuffix(_root, "recognized-dest", "candidate-")
            .Should()
            .BeNull("no candidate may be created once Checkpoint A fails -- it runs before staging/candidate work");
    }

    [Fact]
    public void Deploy_Recognized_CheckpointB_FailsWhenProcessStartsBeforePreserveCopy()
    {
        var staged = CreateStagedDirectory(_root, "chkB1");
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "oldB");
        var exePath = Path.Combine(destination, "LmStreaming.Sample.exe");
        var originalAppsettings = File.ReadAllText(Path.Combine(destination, "appsettings.json"));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateReportingRunningOnCall(2, exePath)}"
        );

        result
            .Succeeded.Should()
            .BeFalse("Checkpoint B must fail before the preserve-list is read from the live destination");
        result.StandardError.Should().Contain("Checkpoint B");
        File.ReadAllText(Path.Combine(destination, "appsettings.json"))
            .Should()
            .Be(originalAppsettings, "destination must be untouched");
        var candidate = FindSiblingWithSuffix(_root, "recognized-dest", "candidate-");
        candidate.Should().NotBeNull("the candidate is retained for inspection, not deleted");
        File.Exists(Path.Combine(candidate!, "appsettings.json"))
            .Should()
            .BeTrue("the replace-set copy (Step 4) must have already happened before Checkpoint B");
        File.Exists(Path.Combine(candidate!, "conversations", "thread-1.json"))
            .Should()
            .BeFalse("the preserve-set copy (Step 6) must NOT have run -- Checkpoint B blocked it");
    }

    [Fact]
    public void Deploy_Recognized_CheckpointC_FailsWhenProcessStartsBeforeSwap()
    {
        var staged = CreateStagedDirectory(_root, "chkC1");
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "oldC");
        var exePath = Path.Combine(destination, "LmStreaming.Sample.exe");
        var originalAppsettings = File.ReadAllText(Path.Combine(destination, "appsettings.json"));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateReportingRunningOnCall(3, exePath)}"
        );

        result.Succeeded.Should().BeFalse("Checkpoint C must fail immediately before the swap");
        result.StandardError.Should().Contain("Checkpoint C");
        File.ReadAllText(Path.Combine(destination, "appsettings.json"))
            .Should()
            .Be(originalAppsettings, "destination must be untouched -- no rename has happened yet");
        var candidate = FindSiblingWithSuffix(_root, "recognized-dest", "candidate-");
        candidate.Should().NotBeNull("the candidate is retained for inspection");
        File.Exists(Path.Combine(candidate!, "conversations", "thread-1.json"))
            .Should()
            .BeTrue("the preserve-set copy (Step 6) must have completed before Checkpoint C");
    }

    [Fact]
    public void Publish_Recognized_CheckpointA_FailsBeforeBuildClientPhase_WhenProcessRunning()
    {
        // Regression for Checkpoint A ordering: Invoke-DestinationPublish used to run the ENTIRE
        // client build phase (Write-Phase "Build client...") BEFORE ever calling
        // Invoke-DestinationDeploy (where Checkpoint A's running-process check lives), so a
        // running destination process wasn't detected until after potentially minutes of wasted
        // npm/dotnet work. Point ClientAppDirectory at a directory that does not exist: if
        // Checkpoint A's early check is NOT wired in ahead of the build phase, the (buggy) code
        // reaches Write-Phase "Build client..." and only then fails (npm ci errors out fast
        // against the bogus prefix) -- fast either way, but the phase marker and the failure
        // reason prove which code path actually ran.
        var destination = CreateRecognizedDestination(_root, "publish-chkA-dest", "oldPubA");
        var exePath = Path.Combine(destination, "LmStreaming.Sample.exe");
        var bogusClientAppDirectory = Path.Combine(_root, "does-not-exist-client-app");
        var bogusProjectFile = Path.Combine(_root, "does-not-exist.csproj");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationPublish -ProjectFile '{PublishLaunchScriptHost.QuoteSingle(bogusProjectFile)}' "
                + $"-ClientAppDirectory '{PublishLaunchScriptHost.QuoteSingle(bogusClientAppDirectory)}' "
                + "-Configuration Debug "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-RepositoryRoot '{PublishLaunchScriptHost.QuoteSingle(_root)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateReportingRunningOnCall(1, exePath)}",
            TimeSpan.FromSeconds(60)
        );

        result.Succeeded.Should().BeFalse("Checkpoint A must reject a running destination executable");
        result
            .StandardError.Should()
            .Contain(
                "Checkpoint A",
                "the early check inside Invoke-DestinationPublish must be the one that fails, not a later build/npm error"
            );
        result
            .StandardOutput.Should()
            .NotContain(
                "Build client",
                "Checkpoint A must run BEFORE the client build phase, so that phase's marker must never be printed"
            );
    }

    // ----------------------------------------------------------------------------------------
    // Rollback on second-rename failure
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Deploy_Recognized_RollbackOnSecondMoveFailure_DestinationByteIdenticalAndCandidateRetained()
    {
        var staged = CreateStagedDirectory(_root, "rollback1");
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "oldRB");
        var appsettingsHashBefore = Hash(Path.Combine(destination, "appsettings.json"));
        var conversationsHashBefore = Hash(Path.Combine(destination, "conversations", "thread-1.json"));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-MoveDelegate {MoveDelegateThatFailsOnCall(2)}"
        );

        result
            .Succeeded.Should()
            .BeFalse("the second rename (candidate -> destination) was made to fail deterministically");
        result.StandardError.Should().Contain("rolled back");

        Directory.Exists(destination).Should().BeTrue("the destination must exist after rollback");
        Hash(Path.Combine(destination, "appsettings.json"))
            .Should()
            .Be(
                appsettingsHashBefore,
                "destination content must be byte-identical to its pre-run state after rollback"
            );
        Hash(Path.Combine(destination, "conversations", "thread-1.json"))
            .Should()
            .Be(conversationsHashBefore, "preserved data must also be byte-identical after rollback");

        FindSiblingWithSuffix(_root, "recognized-dest", "backup-")
            .Should()
            .BeNull("the backup must have been renamed back onto the destination path, so no backup sibling remains");
        var candidate = FindSiblingWithSuffix(_root, "recognized-dest", "candidate-");
        candidate
            .Should()
            .NotBeNull("the candidate must be retained on disk for inspection, not deleted, after a rollback");
        File.ReadAllText(Path.Combine(candidate!, "appsettings.json"))
            .Should()
            .Be(
                "{\"source\":\"staged\"}",
                "the retained candidate is the fully-assembled one from before the failed swap"
            );
    }

    [Fact]
    public void Deploy_Recognized_RecoversFromTransientFirstMoveFailure_ViaRetry()
    {
        // #371: the first rename Invoke-CandidateSwap performs (existing destination -> backup) hit
        // a real, transient ACCESS_DENIED in CI -- a directory renamed moments earlier can briefly
        // be held open by Defender/the search indexer, and that clears on its own within
        // milliseconds. Invoke-AtomicMove now retries a bounded number of times on exactly that
        // failure shape (see the comment above it). This pins the recovery deterministically: the
        // delegate below fails the first two invocations with a real wrapped IOException (not a
        // simulated race -- see MoveDelegateThatFailsTransientlyThenSucceeds), so a correct retry
        // must survive to the third attempt and complete the deploy exactly as if nothing had
        // failed. No wall-clock assertion is made anywhere here (#343): only the outcome -- that the
        // whole deploy still succeeds and lands the expected content -- is checked.
        var staged = CreateStagedDirectory(_root, "transientretry1");
        var destination = CreateRecognizedDestination(_root, "transient-retry-dest", "oldTR");
        var markerFrom = Directory.CreateDirectory(Path.Combine(_root, "transient-marker-from")).FullName;
        var markerExisting = Directory.CreateDirectory(Path.Combine(_root, "transient-marker-existing")).FullName;

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-MoveDelegate {MoveDelegateThatFailsTransientlyThenSucceeds(2, markerFrom, markerExisting)}"
        );

        result
            .Succeeded.Should()
            .BeTrue(
                "two transient IOExceptions on the first move must be absorbed by the retry -- " + result.StandardError
            );
        File.ReadAllText(Path.Combine(destination, "appsettings.json"))
            .Should()
            .Be(
                "{\"source\":\"staged\"}",
                "the deploy must have completed for real, landing the staged content, not merely reported success"
            );
        FindSiblingWithSuffix(_root, "transient-retry-dest", "backup-")
            .Should()
            .BeNull("a successful swap -- retried or not -- must still remove the backup sibling");
        FindSiblingWithSuffix(_root, "transient-retry-dest", "candidate-")
            .Should()
            .BeNull("a successful swap -- retried or not -- must still leave no candidate sibling behind");
    }

    [Fact]
    public void Deploy_Recognized_RecoversFromTransientRollbackMoveFailure_ViaRetry()
    {
        // #396 review (B4): the test above only pins recovery on the FIRST rename (call 1,
        // destination -> backup). Issue #371's ACTUAL flake was on the ROLLBACK move (call 3
        // onward, inside Invoke-CandidateSwap's guarded catch block, ~line 930) -- the same
        // transient sharing-violation shape, but on the rename that only runs after the swap
        // itself has already failed. This pins recovery there specifically: rename 1 succeeds,
        // rename 2 (the swap) fails non-transiently to force entry into the rollback branch, and
        // the rollback move itself then fails with two real, wrapped IOExceptions before
        // Invoke-AtomicMove's retry lets it succeed on its third attempt -- landing the "rolled
        // back" recovery message, not the "ALSO FAILED" double-failure message a permanently
        // failing rollback would produce.
        var staged = CreateStagedDirectory(_root, "transientrollback1");
        var destination = CreateRecognizedDestination(_root, "transient-rollback-dest", "oldTRB");
        var appsettingsHashBefore = Hash(Path.Combine(destination, "appsettings.json"));
        var conversationsHashBefore = Hash(Path.Combine(destination, "conversations", "thread-1.json"));
        var markerFrom = Directory.CreateDirectory(Path.Combine(_root, "transient-rollback-marker-from")).FullName;
        var markerExisting = Directory
            .CreateDirectory(Path.Combine(_root, "transient-rollback-marker-existing"))
            .FullName;

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-MoveDelegate {MoveDelegateThatFailsSwapThenRecoversRollbackViaRetry(2, markerFrom, markerExisting)}"
        );

        result
            .Succeeded.Should()
            .BeFalse(
                "the swap itself was made to fail deterministically, so the deploy as a whole still reports failure even though the rollback recovered"
            );
        result
            .StandardError.Should()
            .Contain(
                "rolled back",
                "two transient IOExceptions on the ROLLBACK move must be absorbed by the same retry that #371 added, landing the successful-rollback message rather than the double-failure one"
            );
        result
            .StandardError.Should()
            .NotContain("ALSO FAILED", "a recovered rollback must not report the double-failure path");

        Directory.Exists(destination).Should().BeTrue("the destination must exist after a recovered rollback");
        Hash(Path.Combine(destination, "appsettings.json"))
            .Should()
            .Be(
                appsettingsHashBefore,
                "destination content must be byte-identical to its pre-run state once the retried rollback completes"
            );
        Hash(Path.Combine(destination, "conversations", "thread-1.json"))
            .Should()
            .Be(conversationsHashBefore, "preserved data must also be byte-identical after the recovered rollback");

        FindSiblingWithSuffix(_root, "transient-rollback-dest", "backup-")
            .Should()
            .BeNull(
                "a recovered rollback -- retried or not -- must still rename the backup back onto the destination path, leaving no backup sibling"
            );
        var candidate = FindSiblingWithSuffix(_root, "transient-rollback-dest", "candidate-");
        candidate
            .Should()
            .NotBeNull(
                "the assembled candidate must still be retained on disk for inspection after a recovered rollback"
            );
    }

    // ----------------------------------------------------------------------------------------
    // No launch, in every writable state
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("recognized")]
    public void Deploy_NeverLaunchesPublishedExecutable_InAnyState(string state)
    {
        var staged = CreateStagedDirectory(_root, "launch-" + state);
        string destination = state switch
        {
            "missing" => Path.Combine(_root, "launch-missing-dest"),
            "empty" => CreateEmptyDestination(_root, "launch-empty-dest"),
            "recognized" => CreateRecognizedDestination(_root, "launch-recognized-dest", "oldL"),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

        // Snapshot PIDs rather than asserting the name is globally absent: this machine can
        // legitimately be running an unrelated "LmStreaming.Sample" process at the same time (e.g.
        // a dev server in another worktree/session) -- the property this test needs is "this
        // deploy call started no NEW instance", not "no instance exists anywhere on the box".
        var pidsBefore = System
            .Diagnostics.Process.GetProcessesByName("LmStreaming.Sample")
            .Select(p => p.Id)
            .ToHashSet();

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        result
            .StandardOutput.Should()
            .NotContain(
                "Launch published executable",
                "Invoke-DestinationDeploy never launches anything -- there is no launch phase reachable from it"
            );
        var pidsAfter = System
            .Diagnostics.Process.GetProcessesByName("LmStreaming.Sample")
            .Select(p => p.Id)
            .ToHashSet();
        pidsAfter
            .Except(pidsBefore)
            .Should()
            .BeEmpty("no NEW real process by that name may be started as a result of this deploy");
    }

    // ----------------------------------------------------------------------------------------
    // Real top-level invocation must actually take the destination branch (not just
    // Invoke-DestinationPublish/Invoke-DestinationDeploy in isolation, which every test above
    // exercises via dot-source + direct function call).
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void RealTopLevelInvocation_DestinationDirectoryBoundOnCommandLine_TakesDestinationBranch_NeverResolvesPort()
    {
        // Regression for a binding bug: Invoke-Main declared no param() block of its own, so its
        // internal $PSBoundParameters was ALWAYS empty regardless of what the script's own
        // top-level command line bound -- $PSBoundParameters.ContainsKey('DestinationDirectory')
        // was unconditionally false inside Invoke-Main, so a real
        // "./publish-launch.ps1 -DestinationDirectory X" invocation silently fell through into the
        // default build+launch pipeline instead of destination-only publish mode. Every other test
        // in this file dot-sources the script and calls a named function (Invoke-DestinationDeploy,
        // Invoke-DestinationPublish, ...) directly, which cannot see this bug -- it lives entirely
        // in the wiring between the script's OWN top-level parameter binding and how Invoke-Main is
        // invoked from the bottom dot-source guard. This test invokes the script itself with the
        // call operator (exactly how a real caller runs it, not dot-sourced), so it is the only
        // test that actually exercises that wiring.
        var destination = CreateUnrecognizedDestination(
            _root,
            "real-invocation-unrecognized-dest",
            hasExe: true,
            hasAppsettings: true,
            hasIndex: false
        );

        var command =
            $"& '{PublishLaunchScriptHost.QuoteSingle(PublishLaunchScriptHost.ScriptPath)}' "
            + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'";
        var result = PublishLaunchScriptHost.Run(command, TimeSpan.FromSeconds(30));

        // Test-DestinationState and the Unrecognized throw run synchronously, before any
        // npm/dotnet work, so a correctly-wired invocation fails fast with this exact message and
        // never reaches port resolution or the client build phase. If the wiring bug is present,
        // none of that happens: the script falls through into the default pipeline instead, which
        // prints "Resolve port" almost immediately and then goes on to attempt a real client
        // build/publish -- something this test's bounded 30s timeout is deliberately too short for,
        // so a regression fails this test either via a wrong-content assertion or via a timeout,
        // not by silently passing.
        result
            .Succeeded.Should()
            .BeFalse(
                "an Unrecognized destination passed via a real top-level -DestinationDirectory invocation must be rejected"
            );
        result
            .StandardError.Should()
            .Contain(
                "Unrecognized",
                "the destination branch, not the default pipeline, must run when -DestinationDirectory is bound on the script's real command line"
            );
        result
            .StandardOutput.Should()
            .NotContain(
                "Resolve port",
                "the default pipeline's port-resolution phase must never run once -DestinationDirectory is explicitly bound"
            );
        result
            .StandardOutput.Should()
            .NotContain(
                "Build client",
                "the default pipeline's client build must never run for an explicit -DestinationDirectory invocation"
            );
    }

    // ----------------------------------------------------------------------------------------
    // Running-instance detection must FAIL CLOSED (Test-ProcessHoldsPath)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void ProcessHoldsPath_LockedExecutable_IsHeld_EvenWhenEnumerationSeesNothing()
    {
        // The signal that actually matters. A running Windows process holds its own image file with
        // a share mode that denies write, and that is the very condition that will make the swap's
        // rename fail -- so an exclusive-open failure means "held" regardless of what process
        // enumeration reports. This arm needs no privilege, which is exactly why it exists: the
        // enumeration arm cannot see an elevated instance at all.
        var exePath = Path.Combine(_root, "locked.exe");
        File.WriteAllText(exePath, "x");

        using var holder = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-ProcessHoldsPath -ExecutablePath '{PublishLaunchScriptHost.QuoteSingle(exePath)}' "
                + "-ProcessEnumerationDelegate { param() return @() }"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        result
            .StandardOutput.Trim()
            .Should()
            .Be(
                "true",
                "a file that cannot be opened exclusively is held by something, and the rename this predicate guards would fail -- enumeration returning nothing must not override that"
            );
    }

    [Fact]
    public void ProcessHoldsPath_UnlockedExecutableAndNoMatchingProcess_IsNotHeld()
    {
        // Non-vacuity guard for the two tests around it: with a genuinely free executable and an
        // empty enumeration the predicate must be FALSE. Without this, a bug making
        // Test-ProcessHoldsPath unconditionally true would leave every other test here green while
        // refusing every real deploy.
        var exePath = Path.Combine(_root, "free.exe");
        File.WriteAllText(exePath, "x");

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-ProcessHoldsPath -ExecutablePath '{PublishLaunchScriptHost.QuoteSingle(exePath)}' "
                + "-ProcessEnumerationDelegate { param() return @() }"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        result.StandardOutput.Trim().Should().Be("false");
    }

    [Fact]
    public void ProcessHoldsPath_ElevatedInstanceWhosePathIsUnreadable_IsHeld()
    {
        // THE regression this fail-closed rework exists for. Get-Process cannot read `.Path` for a
        // process running elevated or as another user -- the getter throws Win32Exception "Access is
        // denied" -- and the old implementation swallowed that into `continue`, silently converting
        // "cannot determine" into "not a match". Deploying over an elevated instance therefore sailed
        // straight past all three checkpoints and into the swap.
        var exePath = Path.Combine(_root, "elevated.exe");
        File.WriteAllText(exePath, "x");

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-ProcessHoldsPath -ExecutablePath '{PublishLaunchScriptHost.QuoteSingle(exePath)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateWithUnreadablePath("elevated")} "
                + $"-ExclusiveOpenProbeDelegate {ProbeDelegateReportingNotHeld}"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        result
            .StandardOutput.Trim()
            .Should()
            .Be(
                "true",
                "a process whose NAME matches the target executable but whose path cannot be read is indeterminate, and indeterminate must count as held"
            );
    }

    [Fact]
    public void ProcessHoldsPath_UnrelatedProcessWhosePathIsUnreadable_IsNotHeld()
    {
        // The necessary bound on the previous test. On any Windows box a non-elevated Get-Process
        // enumerates dozens of processes whose `.Path` throws (System, Registry, csrss, anything
        // running as SYSTEM). Treating EVERY unreadable path as a match would make this predicate
        // permanently true and refuse every deploy on every machine -- a fail-closed rework that
        // closes the door on the operator too. Only a NAME match may escalate to "held".
        var exePath = Path.Combine(_root, "target.exe");
        File.WriteAllText(exePath, "x");

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-ProcessHoldsPath -ExecutablePath '{PublishLaunchScriptHost.QuoteSingle(exePath)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateWithUnreadablePath("csrss")} "
                + $"-ExclusiveOpenProbeDelegate {ProbeDelegateReportingNotHeld}"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        result
            .StandardOutput.Trim()
            .Should()
            .Be(
                "false",
                "an unrelated SYSTEM process that merely refuses to reveal its path must not be mistaken for our executable"
            );
    }

    [Fact]
    public void Deploy_Recognized_CheckpointA_FailsWhenExecutableIsLockedButNoProcessMatches()
    {
        // End-to-end proof that the lock probe is actually wired into the deploy checkpoints, not
        // just unit-testable in isolation: enumeration reports nothing (the elevated-instance case),
        // yet the deploy must still refuse and leave the destination untouched.
        var staged = CreateStagedDirectory(_root, "lockedchk1");
        var destination = CreateRecognizedDestination(_root, "locked-chk-dest", "oldLK");
        var exePath = Path.Combine(destination, "LmStreaming.Sample.exe");
        var exeHashBefore = Hash(exePath);

        using (var holder = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = PublishLaunchScriptHost.InvokeForEffect(
                $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                    + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                    + "-ProcessEnumerationDelegate { param() return @() }"
            );

            result.Succeeded.Should().BeFalse("the destination executable is locked, so the swap's rename would fail");
            result.StandardError.Should().Contain("Checkpoint A");
        }

        Hash(exePath)
            .Should()
            .Be(exeHashBefore, "the destination must be entirely untouched when a checkpoint refuses");
        FindSiblingWithSuffix(_root, "locked-chk-dest", "candidate-")
            .Should()
            .BeNull("Checkpoint A runs before any candidate is created");
    }

    // ----------------------------------------------------------------------------------------
    // Classifier / validator must not drift apart
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void ConfirmPublishArtifact_RejectsArtifactMissingAppsettings_SameMarkerSetAsClassifier()
    {
        // These two used to disagree: Test-DestinationState required appsettings.json to call a
        // directory "ours", Confirm-PublishArtifact did not check it at all. A publish that produced
        // no appsettings.json therefore passed validation, deployed, had its backup deleted -- and
        // then classified as UNRECOGNIZED on the very next run, locking the operator out of their own
        // deploy directory behind an error calling it foreign, with the previous deployment already
        // gone. Both now derive from $script:DestinationMarkerRelativePaths, so validation refuses
        // while the previous deployment is still recoverable.
        var staged = CreateStagedDirectory(_root, "nosettings1");
        File.Delete(Path.Combine(staged, "appsettings.json"));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Confirm-PublishArtifact -PublishDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}'"
        );

        result
            .Succeeded.Should()
            .BeFalse("an artifact the classifier would later call Unrecognized must fail validation now");
        result.StandardError.Should().Contain("appsettings.json");
    }

    [Fact]
    public void ConfirmPublishArtifact_AcceptsCompleteArtifact()
    {
        // Non-vacuity guard: the marker loop must not reject a well-formed artifact.
        var staged = CreateStagedDirectory(_root, "complete1");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Confirm-PublishArtifact -PublishDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
    }

    [Fact]
    public void Deploy_StagedArtifactMissingAppsettings_RefusesBeforeTouchingRecognizedDestination()
    {
        // The consequence, end to end: the candidate is validated BEFORE the swap, so an incomplete
        // publish can never reach the destination and can never delete the backup.
        var staged = CreateStagedDirectory(_root, "nosettings2");
        File.Delete(Path.Combine(staged, "appsettings.json"));
        var destination = CreateRecognizedDestination(_root, "nosettings-dest", "oldNS");
        var appsettingsHashBefore = Hash(Path.Combine(destination, "appsettings.json"));
        var envHashBefore = Hash(Path.Combine(destination, ".env"));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeFalse("the assembled candidate is not a complete artifact");
        Hash(Path.Combine(destination, "appsettings.json"))
            .Should()
            .Be(appsettingsHashBefore, "the destination must be untouched");
        Hash(Path.Combine(destination, ".env")).Should().Be(envHashBefore, "preserved data must be untouched");
        FindSiblingWithSuffix(_root, "nosettings-dest", "backup-")
            .Should()
            .BeNull("no rename ever happened, so no backup exists");
    }

    // ----------------------------------------------------------------------------------------
    // Interrupted-deploy recovery (orphaned siblings beside a Missing destination)
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("backup-20260101T000000000Z-1234")]
    [InlineData("candidate-20260101T000000000Z-1234")]
    public void Deploy_MissingDestinationWithOrphanedSibling_RefusesAndNamesTheRecoveryPath(string suffix)
    {
        // A deploy interrupted between its two renames (crash, Ctrl-C, power loss) leaves the
        // destination path NON-EXISTENT with its backup and/or candidate still beside it. Re-running
        // would classify Missing and take the fresh-install branch -- which never calls
        // Copy-PreserveSet -- silently deploying an empty instance and stranding the operator's only
        // copy of .env, oauth-tokens/ and conversations/ in a sibling nothing would ever mention.
        var staged = CreateStagedDirectory(_root, "orphan1");
        var destination = Path.Combine(_root, "orphan-dest");
        var orphan = Path.Combine(_root, "orphan-dest." + suffix);
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, ".env"), "PRECIOUS=1");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}"
        );

        result
            .Succeeded.Should()
            .BeFalse(
                "a Missing destination with leftover deploy siblings is an interrupted deploy, not a fresh install"
            );
        result
            .StandardError.Should()
            .Contain(orphan, "the error must name the exact sibling so the operator knows what to rename back");
        Directory
            .Exists(destination)
            .Should()
            .BeFalse("nothing may be deployed while the interrupted state is unresolved");
        File.ReadAllText(Path.Combine(orphan, ".env"))
            .Should()
            .Be("PRECIOUS=1", "the orphaned sibling must be left exactly as found");
    }

    [Fact]
    public void Deploy_MissingDestinationWithUnrelatedSibling_ProceedsNormally()
    {
        // Bound on the check above: only OUR suffixes count. A directory that merely lives in the
        // same parent must not block a legitimate first deploy.
        var staged = CreateStagedDirectory(_root, "orphan2");
        Directory.CreateDirectory(Path.Combine(_root, "orphan2-dest-unrelated"));
        Directory.CreateDirectory(Path.Combine(_root, "orphan2-dest.notours-123"));
        var destination = Path.Combine(_root, "orphan2-dest");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        Directory.Exists(destination).Should().BeTrue();
    }

    // ----------------------------------------------------------------------------------------
    // Fresh-deploy warning (no .env is preserved because there is nothing to preserve from)
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    public void Deploy_FreshInstall_WarnsThatNoEnvWillBePresent_ButStillSucceeds(string state)
    {
        // Missing and Empty both take a branch that never calls Copy-PreserveSet, so the deployed
        // instance has no .env at all. The app starts fine and then fails every provider request
        // with an auth error that says nothing about a missing file. Warn, loudly, naming the path.
        //
        // Deliberately a WARNING and deliberately no seeded placeholder: a first-ever deploy to a
        // new machine is legitimate, and a seeded .env would be indistinguishable from a real one --
        // shadowing the operator's own copy and then being preserved forever by Copy-PreserveSet.
        var staged = CreateStagedDirectory(_root, "fresh-" + state);
        var destination =
            state == "missing"
                ? Path.Combine(_root, "fresh-missing-dest")
                : CreateEmptyDestination(_root, "fresh-empty-dest");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' "
                + $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}"
        );

        result
            .Succeeded.Should()
            .BeTrue("a fresh install is legitimate and must not be blocked: " + result.StandardError);

        // Write-Warning under `pwsh -Command` lands on STDOUT ("WARNING: ..."), not stderr --
        // verified directly rather than assumed; asserting on stderr silently passed nothing.
        result
            .StandardOutput.Should()
            .Contain("WARNING", "the fresh-install notice must be a real warning, not a quiet Write-Host line");
        result.StandardOutput.Should().Contain(".env", "the warning must name what is missing");
        result.StandardOutput.Should().Contain(destination, "the warning must name the exact deployment it applies to");
        File.Exists(Path.Combine(destination, ".env"))
            .Should()
            .BeFalse(
                "no placeholder .env may be seeded -- it would be indistinguishable from a real one and preserved forever"
            );
    }

    [Fact]
    public void Deploy_Recognized_DoesNotEmitTheFreshInstallWarning()
    {
        // Non-vacuity bound: an upgrade DOES preserve .env, so the warning must not fire. Without
        // this, an unconditional warning would satisfy both cases above and mean nothing.
        var staged = CreateStagedDirectory(_root, "nowarn1");
        var destination = CreateRecognizedDestination(_root, "nowarn-dest", "oldNW");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' "
                + $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        result
            .StandardOutput.Should()
            .NotContain("Fresh deployment", "an upgrade preserves .env, so the fresh-install warning must not fire");
        File.Exists(Path.Combine(destination, ".env")).Should().BeTrue();
    }

    // ----------------------------------------------------------------------------------------
    // Destination path normalization
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void ResolveDestinationDirectory_RelativePath_ResolvesAgainstCallerWorkingDirectory()
    {
        // Every sibling path is derived with Split-Path -Parent, which returns '' for a bare
        // relative name -- and Join-Path then throws on the empty parent, deep inside the swap
        // rather than at the entry point. Normalizing once, up front, is what keeps the sibling
        // derivation total.
        // A script-block invocation, not "Set-Location; Resolve-..." -- InvokeForJson wraps the
        // expression in parentheses, and a parenthesized expression cannot contain a statement
        // separator.
        var result = PublishLaunchScriptHost.InvokeForJson(
            $"& {{ Set-Location -LiteralPath '{PublishLaunchScriptHost.QuoteSingle(_root)}'; "
                + "Resolve-DestinationDirectory -DestinationDirectory 'relative-deploy' }"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        result
            .StandardOutput.Trim()
            .Trim('"')
            .Should()
            .Be(
                Path.Combine(_root, "relative-deploy").Replace("\\", "\\\\"),
                "a relative destination must resolve against the caller's working directory, absolutely"
            );
    }

    [Fact]
    public void ResolveDestinationDirectory_TrailingSeparator_IsTrimmed()
    {
        // Split-Path -Leaf returns '' for "C:\deploy\", which would produce sibling names like
        // ".candidate-..." with no leaf prefix -- and FindSiblingWithSuffix-style recovery guidance
        // would then never match them.
        var withSlash = Path.Combine(_root, "trailing-deploy") + Path.DirectorySeparatorChar;

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Resolve-DestinationDirectory -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(withSlash)}'"
        );

        result.Succeeded.Should().BeTrue(result.StandardError);
        result
            .StandardOutput.Trim()
            .Trim('"')
            .Should()
            .Be(Path.Combine(_root, "trailing-deploy").Replace("\\", "\\\\"));
    }

    [Fact]
    public void ResolveDestinationDirectory_DriveRoot_IsRefused()
    {
        // A drive root has no parent, so there is nowhere to place the candidate and backup
        // siblings -- and a deploy there would treat the entire volume as the deployment.
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Resolve-DestinationDirectory -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(driveRoot)}'"
        );

        result
            .Succeeded.Should()
            .BeFalse("deploying to a volume root must be refused explicitly, not fail obscurely inside the swap");
        result.StandardError.Should().Contain("root");
    }

    // ----------------------------------------------------------------------------------------
    // Script-host contract (the gate for #340)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void ScriptHost_ThrownMessage_ReachesStderrVerbatim_NotAsRenderedConsoleOutput()
    {
        // Every "the error must name the exact path" assertion in this file rests on one property:
        // what PublishLaunchScriptHost puts in StandardError is the string the script threw, not
        // pwsh's rendering of it. That property used to hold only by luck: pwsh's ConciseView
        // formatter decorates an unhandled terminating error on its way to stderr, and on a
        // machine whose profile directory contains a space, that decoration once corrupted an
        // otherwise-correct message and three tests here failed while the script behaved
        // perfectly (#340).
        //
        // This test does not exercise ConciseView's word-wrap directly: the throw below runs at
        // `-Command` top level, inside CaptureStructurally's own try/catch (see
        // PublishLaunchScriptHost.cs), and that wrap-and-gutter rendering only appears for a throw
        // inside a dot-sourced script FILE -- probed directly, this shape instead renders unwrapped
        // as "Exception: <message>" when nothing catches it. What this test actually pins is
        // narrower and still real: CaptureStructurally emits `$_.Exception.Message` with no
        // formatter-added text at all, not even that unwrapped "Exception: " prefix. The
        // exact-equality assertion below rejects that prefix exactly as it would reject a wrap, so
        // CaptureStructurally regressing to the raw, unhandled-error path still turns this test
        // red -- just via a prefix mismatch rather than a wrap.
        const string message =
            @"Recovery required: rename 'C:\Program Files\Some Deployment Directory\app.backup-20260101T000000000Z-1234' back onto 'C:\Program Files\Some Deployment Directory\app' to recover.";

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"& {{ throw '{PublishLaunchScriptHost.QuoteSingle(message)}' }}"
        );

        result.Succeeded.Should().BeFalse("a thrown message must still make the invocation fail");
        result
            .StandardError.Trim('\r', '\n')
            .Should()
            .Be(
                message,
                "stderr must carry the message the script composed, byte for byte -- not a formatter-rendered version of it, wrapped or otherwise"
            );
    }

    // ----------------------------------------------------------------------------------------
    // Whole-script syntax
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Script_ParsesWithoutSyntaxErrors()
    {
        // A real parse, not a brace/paren tally. A balanced-delimiter count cannot see an unclosed
        // string, a malformed param() block, a bad here-string terminator, or a delimiter inside a
        // comment -- all of which this file now contains plenty of, and any of which would make the
        // script fail at dot-source time on the operator's machine rather than here.
        var result = PublishLaunchScriptHost.Run(
            "$e = $null; $t = $null; "
                + $"[System.Management.Automation.Language.Parser]::ParseFile('{PublishLaunchScriptHost.QuoteSingle(PublishLaunchScriptHost.ScriptPath)}', [ref]$t, [ref]$e) | Out-Null; "
                + "if ($e) { $e | ForEach-Object { \"$($_.Extent.StartLineNumber): $($_.Message)\" }; exit 1 }"
        );

        result
            .Succeeded.Should()
            .BeTrue("publish-launch.ps1 must parse cleanly: " + result.StandardOutput + result.StandardError);
    }
}
