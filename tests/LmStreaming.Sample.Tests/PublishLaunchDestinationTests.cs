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
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "pldest-" + Guid.NewGuid().ToString("N"))).FullName;

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
            $"<html><body><script src=\"/dist/assets/{assetFileName}\"></script></body></html>");

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
        bool seedNotifyWaitsDb = true)
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
            $"<html><body><script src=\"/dist/assets/{oldAssetFileName}\"></script></body></html>");

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
            await store.SaveAsync(new NotifyWaitRecord(
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
                Status: "active"));

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
        bool hasIndex)
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
        Directory.GetDirectories(parent)
            .FirstOrDefault(d => Path.GetFileName(d).StartsWith($"{destinationName}.{suffix}", StringComparison.Ordinal));

    private static string ProcessDelegateThatThrowsIfInvoked =>
        "{ param() throw 'TEST FAILURE: process enumeration must not be invoked for this destination state' }";

    private static string ProcessDelegateReportingRunningOnCall(int callNumber, string executablePath)
    {
        var quotedPath = PublishLaunchScriptHost.QuoteSingle(executablePath);
        return "{ param() " +
            "if (-not (Get-Variable -Name __pldCallCount -Scope Script -ErrorAction SilentlyContinue)) { $script:__pldCallCount = 0 }; " +
            "$script:__pldCallCount++; " +
            $"if ($script:__pldCallCount -eq {callNumber}) {{ return @([pscustomobject]@{{ Id = 4321; Path = '{quotedPath}' }}) }}; " +
            "return @() }";
    }

    private static string MoveDelegateThatFailsOnCall(int callNumber) =>
        "{ param($From, $To) " +
        "if (-not (Get-Variable -Name __pldMoveCallCount -Scope Script -ErrorAction SilentlyContinue)) { $script:__pldMoveCallCount = 0 }; " +
        "$script:__pldMoveCallCount++; " +
        $"if ($script:__pldMoveCallCount -eq {callNumber}) {{ throw ('Simulated move failure on call ' + $script:__pldMoveCallCount) }}; " +
        "[System.IO.Directory]::Move($From, $To) }";

    // ----------------------------------------------------------------------------------------
    // Destination classification (Test-DestinationState)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void DestinationState_Missing_WhenPathDoesNotExist()
    {
        var missingPath = Path.Combine(_root, "does-not-exist");

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(missingPath)}'");

        result.Succeeded.Should().BeTrue(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        json.RootElement.GetProperty("State").GetString().Should().Be("Missing");
    }

    [Fact]
    public void DestinationState_Empty_WhenDirectoryHasZeroEntries()
    {
        var empty = CreateEmptyDestination(_root, "empty-dest");

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(empty)}'");

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
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'");

        result.Succeeded.Should().BeTrue(result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        json.RootElement.GetProperty("State").GetString().Should().Be(
            "Unrecognized",
            "a hidden entry still counts as an entry -- the directory is not empty and has no recognition markers");
    }

    [Fact]
    public void DestinationState_Recognized_WhenAllThreeMarkersPresent()
    {
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "abc111");

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'");

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
        bool hasIndex)
    {
        var destination = CreateUnrecognizedDestination(
            _root,
            $"unrecognized-{hasExe}-{hasAppsettings}-{hasIndex}",
            hasExe,
            hasAppsettings,
            hasIndex);

        var result = PublishLaunchScriptHost.InvokeForJson(
            $"Test-DestinationState -DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'");

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
        var destination = CreateUnrecognizedDestination(_root, "unrecognized-dest", hasExe: true, hasAppsettings: true, hasIndex: false);
        var sentinelBefore = File.ReadAllText(Path.Combine(destination, "LmStreaming.Sample.exe"));
        var entriesBefore = Directory.GetFileSystemEntries(destination).Length;

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' " +
            $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}");

        result.Succeeded.Should().BeFalse("an Unrecognized non-empty destination must be rejected");
        result.StandardError.Should().Contain("Unrecognized");
        File.ReadAllText(Path.Combine(destination, "LmStreaming.Sample.exe")).Should().Be(sentinelBefore, "the destination must be untouched");
        Directory.GetFileSystemEntries(destination).Length.Should().Be(entriesBefore, "no candidate/backup work should have started");
        FindSiblingWithSuffix(_root, "unrecognized-dest", "candidate-").Should().BeNull("no candidate sibling may ever be created for an Unrecognized destination");
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
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' " +
            $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}");

        result.Succeeded.Should().BeTrue(result.StandardError);
        Directory.Exists(destination).Should().BeTrue();
        File.Exists(Path.Combine(destination, "LmStreaming.Sample.exe")).Should().BeTrue("the Missing-destination deploy must copy the staged output byte-for-byte");
        Hash(Path.Combine(destination, "LmStreaming.Sample.exe")).Should().Be(expectedExeHash, "the Missing-destination deploy must copy the staged output byte-for-byte");
        File.Exists(Path.Combine(destination, "wwwroot", "dist", "assets", "app.missing1.js")).Should().BeTrue("the staged asset must survive the swap unchanged");
        Hash(Path.Combine(destination, "wwwroot", "dist", "assets", "app.missing1.js")).Should().Be(expectedAssetHash, "the staged asset must survive the swap unchanged");

        // The single move is FROM A CANDIDATE ASSEMBLED AS A SAME-PARENT SIBLING OF THE
        // DESTINATION, not from $StagedDirectory directly: $StagedDirectory can live on a
        // different volume than the destination (e.g. the repo's own scratchpad tree vs. an
        // external -DestinationDirectory), and Directory.Move cannot cross volumes. Renaming
        // staged itself would also consume it, unlike every other branch (Empty/Recognized both
        // leave $StagedDirectory intact for the caller). So staged must still exist afterwards,
        // and no leftover candidate/backup sibling should remain once the single rename succeeds.
        Directory.Exists(staged).Should().BeTrue("the staged directory must be left intact by the deploy, exactly like the Empty and Recognized branches -- only a same-parent candidate assembled from it is ever moved");
        FindSiblingWithSuffix(_root, "missing-dest", "candidate-").Should().BeNull("the candidate must have been renamed into place, leaving no leftover candidate sibling");
        FindSiblingWithSuffix(_root, "missing-dest", "backup-").Should().BeNull("there was nothing pre-existing to back up for a Missing destination");
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
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' " +
            $"-MoveDelegate {MoveDelegateThatFailsOnCall(2)}");

        result.Succeeded.Should().BeFalse("the second rename (candidate -> destination) was made to fail deterministically");
        result.StandardError.Should().Contain("rolled back");

        Directory.Exists(destination).Should().BeTrue("the destination must exist after rollback");
        Directory.GetFileSystemEntries(destination).Should().BeEmpty("the destination was empty before this run and must be byte-identical (empty) after rollback");

        FindSiblingWithSuffix(_root, "empty-rollback-dest", "backup-").Should().BeNull("the backup must have been renamed back onto the destination path, so no backup sibling remains");
        var candidate = FindSiblingWithSuffix(_root, "empty-rollback-dest", "candidate-");
        candidate.Should().NotBeNull("the candidate must be retained on disk for inspection, not deleted, after a rollback");
        File.ReadAllText(Path.Combine(candidate!, "appsettings.json")).Should().Be("{\"source\":\"staged\"}", "the retained candidate is the fully-assembled one from before the failed swap");
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
            "STALE STAGED WAL -- must never leak into the destination");

        // seedNotifyWaitsDb: false -- the existing destination has no notify-waits.db (and
        // therefore no sidecars) at all, so Copy-PreserveSet's own sidecar copy is a no-op here;
        // only Copy-ReplaceSet's exclusion can prevent the leak.
        var destination = CreateRecognizedDestination(_root, "sidecar-leak-dest", "oldSC", seedNotifyWaitsDb: false);

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'");

        result.Succeeded.Should().BeTrue(result.StandardError);
        File.Exists(Path.Combine(destination, "notify-waits.db-wal")).Should().BeFalse(
            "a stray sidecar present in the staged output must never leak into the destination -- Copy-ReplaceSet must exclude notify-waits.db's sidecar names, not just the bare db filename");
    }

    [Fact]
    public void Deploy_Empty_PerformsTwoRenameSwap_ByteIdenticalToStaged_BackupRemoved()
    {
        var staged = CreateStagedDirectory(_root, "empty1");
        var destination = CreateEmptyDestination(_root, "empty-dest");

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' " +
            $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}");

        result.Succeeded.Should().BeTrue(result.StandardError);
        AssertByteIdentical(
            Path.Combine(staged, "LmStreaming.Sample.exe"),
            Path.Combine(destination, "LmStreaming.Sample.exe"),
            "the Empty-destination deploy must land the staged output byte-for-byte");
        FindSiblingWithSuffix(_root, "empty-dest", "backup-").Should().BeNull("the backup (the old empty directory) must be removed after a successful swap");
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
            relative => Hash(Path.Combine(destination, relative)));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'");

        result.Succeeded.Should().BeTrue(result.StandardError);

        foreach (var relative in preserveRelativeFiles)
        {
            Hash(Path.Combine(destination, relative)).Should().Be(
                hashesBefore[relative],
                $"preserved path '{relative}' must be byte-identical after the swap");
        }

        // Replace-set must have come from staging.
        File.ReadAllText(Path.Combine(destination, "appsettings.json")).Should().Be("{\"source\":\"staged\"}");
        File.ReadAllText(Path.Combine(destination, "LmStreaming.Sample.exe")).Should().StartWith("staged-exe-");

        // Functional coherence proof (not only a byte hash): open the post-swap notify-waits.db
        // through the real production store and confirm the previously-committed row is readable.
        var readBack = await ReadNotifyWaitsAsync(Path.Combine(destination, "notify-waits.db"));
        readBack.Should().ContainSingle(r => r.WaitId == "wait-1" && r.ThreadId == "thread-1" && r.Label == "preserve-test");
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
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'");

        result.Succeeded.Should().BeTrue(result.StandardError);
        var finalEnv = File.ReadAllText(Path.Combine(destination, ".env"));
        finalEnv.Should().Be("REAL_ENV=42", "the destination .env must be the pre-existing one, never the staged decoy");
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
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'");

        result.Succeeded.Should().BeTrue(result.StandardError);
        File.Exists(oldAssetPath).Should().BeFalse("the old hashed Vite asset must not survive -- the candidate's wwwroot/dist is built solely from the fresh staged output");
        File.Exists(Path.Combine(destination, "wwwroot", "dist", "assets", "app.newhash1.js")).Should().BeTrue("the new hashed asset must be present");
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
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(stagedForMissing)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(missingDestination)}' " +
            $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}");
        missingResult.Succeeded.Should().BeTrue(missingResult.StandardError);

        var stagedForEmpty = CreateStagedDirectory(_root, "chke1");
        var emptyDestination = CreateEmptyDestination(_root, "empty-nocheck-dest");
        var emptyResult = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(stagedForEmpty)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(emptyDestination)}' " +
            $"-ProcessEnumerationDelegate {ProcessDelegateThatThrowsIfInvoked}");
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
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' " +
            $"-ProcessEnumerationDelegate {ProcessDelegateReportingRunningOnCall(1, exePath)}");

        result.Succeeded.Should().BeFalse("Checkpoint A must fail fast when the destination executable is running");
        result.StandardError.Should().Contain("Checkpoint A");
        File.ReadAllText(exePath).Should().Be(originalExeContent, "destination must be untouched");
        FindSiblingWithSuffix(_root, "recognized-dest", "candidate-").Should().BeNull("no candidate may be created once Checkpoint A fails -- it runs before staging/candidate work");
    }

    [Fact]
    public void Deploy_Recognized_CheckpointB_FailsWhenProcessStartsBeforePreserveCopy()
    {
        var staged = CreateStagedDirectory(_root, "chkB1");
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "oldB");
        var exePath = Path.Combine(destination, "LmStreaming.Sample.exe");
        var originalAppsettings = File.ReadAllText(Path.Combine(destination, "appsettings.json"));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' " +
            $"-ProcessEnumerationDelegate {ProcessDelegateReportingRunningOnCall(2, exePath)}");

        result.Succeeded.Should().BeFalse("Checkpoint B must fail before the preserve-list is read from the live destination");
        result.StandardError.Should().Contain("Checkpoint B");
        File.ReadAllText(Path.Combine(destination, "appsettings.json")).Should().Be(originalAppsettings, "destination must be untouched");
        var candidate = FindSiblingWithSuffix(_root, "recognized-dest", "candidate-");
        candidate.Should().NotBeNull("the candidate is retained for inspection, not deleted");
        File.Exists(Path.Combine(candidate!, "appsettings.json")).Should().BeTrue("the replace-set copy (Step 4) must have already happened before Checkpoint B");
        File.Exists(Path.Combine(candidate!, "conversations", "thread-1.json")).Should().BeFalse("the preserve-set copy (Step 6) must NOT have run -- Checkpoint B blocked it");
    }

    [Fact]
    public void Deploy_Recognized_CheckpointC_FailsWhenProcessStartsBeforeSwap()
    {
        var staged = CreateStagedDirectory(_root, "chkC1");
        var destination = CreateRecognizedDestination(_root, "recognized-dest", "oldC");
        var exePath = Path.Combine(destination, "LmStreaming.Sample.exe");
        var originalAppsettings = File.ReadAllText(Path.Combine(destination, "appsettings.json"));

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' " +
            $"-ProcessEnumerationDelegate {ProcessDelegateReportingRunningOnCall(3, exePath)}");

        result.Succeeded.Should().BeFalse("Checkpoint C must fail immediately before the swap");
        result.StandardError.Should().Contain("Checkpoint C");
        File.ReadAllText(Path.Combine(destination, "appsettings.json")).Should().Be(originalAppsettings, "destination must be untouched -- no rename has happened yet");
        var candidate = FindSiblingWithSuffix(_root, "recognized-dest", "candidate-");
        candidate.Should().NotBeNull("the candidate is retained for inspection");
        File.Exists(Path.Combine(candidate!, "conversations", "thread-1.json")).Should().BeTrue("the preserve-set copy (Step 6) must have completed before Checkpoint C");
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
            $"Invoke-DestinationPublish -ProjectFile '{PublishLaunchScriptHost.QuoteSingle(bogusProjectFile)}' " +
            $"-ClientAppDirectory '{PublishLaunchScriptHost.QuoteSingle(bogusClientAppDirectory)}' " +
            "-Configuration Debug " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' " +
            $"-RepositoryRoot '{PublishLaunchScriptHost.QuoteSingle(_root)}' " +
            $"-ProcessEnumerationDelegate {ProcessDelegateReportingRunningOnCall(1, exePath)}",
            TimeSpan.FromSeconds(60));

        result.Succeeded.Should().BeFalse("Checkpoint A must reject a running destination executable");
        result.StandardError.Should().Contain("Checkpoint A", "the early check inside Invoke-DestinationPublish must be the one that fails, not a later build/npm error");
        result.StandardOutput.Should().NotContain("Build client", "Checkpoint A must run BEFORE the client build phase, so that phase's marker must never be printed");
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
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}' " +
            $"-MoveDelegate {MoveDelegateThatFailsOnCall(2)}");

        result.Succeeded.Should().BeFalse("the second rename (candidate -> destination) was made to fail deterministically");
        result.StandardError.Should().Contain("rolled back");

        Directory.Exists(destination).Should().BeTrue("the destination must exist after rollback");
        Hash(Path.Combine(destination, "appsettings.json")).Should().Be(appsettingsHashBefore, "destination content must be byte-identical to its pre-run state after rollback");
        Hash(Path.Combine(destination, "conversations", "thread-1.json")).Should().Be(conversationsHashBefore, "preserved data must also be byte-identical after rollback");

        FindSiblingWithSuffix(_root, "recognized-dest", "backup-").Should().BeNull("the backup must have been renamed back onto the destination path, so no backup sibling remains");
        var candidate = FindSiblingWithSuffix(_root, "recognized-dest", "candidate-");
        candidate.Should().NotBeNull("the candidate must be retained on disk for inspection, not deleted, after a rollback");
        File.ReadAllText(Path.Combine(candidate!, "appsettings.json")).Should().Be("{\"source\":\"staged\"}", "the retained candidate is the fully-assembled one from before the failed swap");
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
        var pidsBefore = System.Diagnostics.Process.GetProcessesByName("LmStreaming.Sample").Select(p => p.Id).ToHashSet();

        var result = PublishLaunchScriptHost.InvokeForEffect(
            $"Invoke-DestinationDeploy -StagedDirectory '{PublishLaunchScriptHost.QuoteSingle(staged)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'");

        result.Succeeded.Should().BeTrue(result.StandardError);
        result.StandardOutput.Should().NotContain("Launch published executable", "Invoke-DestinationDeploy never launches anything -- there is no launch phase reachable from it");
        var pidsAfter = System.Diagnostics.Process.GetProcessesByName("LmStreaming.Sample").Select(p => p.Id).ToHashSet();
        pidsAfter.Except(pidsBefore).Should().BeEmpty("no NEW real process by that name may be started as a result of this deploy");
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
            _root, "real-invocation-unrecognized-dest", hasExe: true, hasAppsettings: true, hasIndex: false);

        var command =
            $"& '{PublishLaunchScriptHost.QuoteSingle(PublishLaunchScriptHost.ScriptPath)}' " +
            $"-DestinationDirectory '{PublishLaunchScriptHost.QuoteSingle(destination)}'";
        var result = PublishLaunchScriptHost.Run(command, TimeSpan.FromSeconds(30));

        // Test-DestinationState and the Unrecognized throw run synchronously, before any
        // npm/dotnet work, so a correctly-wired invocation fails fast with this exact message and
        // never reaches port resolution or the client build phase. If the wiring bug is present,
        // none of that happens: the script falls through into the default pipeline instead, which
        // prints "Resolve port" almost immediately and then goes on to attempt a real client
        // build/publish -- something this test's bounded 30s timeout is deliberately too short for,
        // so a regression fails this test either via a wrong-content assertion or via a timeout,
        // not by silently passing.
        result.Succeeded.Should().BeFalse("an Unrecognized destination passed via a real top-level -DestinationDirectory invocation must be rejected");
        result.StandardError.Should().Contain("Unrecognized", "the destination branch, not the default pipeline, must run when -DestinationDirectory is bound on the script's real command line");
        result.StandardOutput.Should().NotContain("Resolve port", "the default pipeline's port-resolution phase must never run once -DestinationDirectory is explicitly bound");
        result.StandardOutput.Should().NotContain("Build client", "the default pipeline's client build must never run for an explicit -DestinationDirectory invocation");
    }
}
