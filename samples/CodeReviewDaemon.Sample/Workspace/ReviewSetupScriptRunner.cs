using System.Globalization;
using System.Text.Json;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

internal interface IReviewSlotPreparer
{
    Task<PreparedCheckout> PrepareAsync(
        ReviewRun run,
        ReviewSlot slot,
        RepoIdentity repository,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Calls the installed, LLM-managed SourcePool implementation remotely. The small read-only verification
/// adapter uses that implementation's lifecycle/slot/source locks and derives store names from .gitmodules,
/// not from a second C# implementation of SourcePool.
/// </summary>
internal sealed class ReviewSetupScriptRunner : IReviewSlotPreparer
{
    internal const string ScriptsRelativePath = ".claude/skills/review-setup/scripts";
    internal const string SetupScript = """
        import sys
        from pathlib import Path
        sys.path.insert(0, str(Path.cwd() / '.claude/skills/review-setup/scripts'))
        from review_common import lock_session
        from review_setup import main
        # The installed router defaults to zero lock wait. Share a bounded budget
        # across its nested scopes so different slots can contend for the source store.
        with lock_session(1200):
            raise SystemExit(main(sys.argv[1:]))
        """;
    internal const string EvidenceScript = """
        import json, sys
        from pathlib import Path
        sys.path.insert(0, str(Path.cwd() / '.claude/skills/review-setup/scripts'))
        from review_common import ReviewState, SourcePool, lock_session, run_git_read, history_coverage, read_submodules, validate_relative_worktree
        from review_pool_admin import select_module_by_name, _pool_status
        root = Path.cwd().resolve()
        state = ReviewState(root, read_only=True)
        request = json.loads(sys.argv[1])
        repo, number = request['repository'], request['slot']
        pool = SourcePool(state, select_module_by_name(root, repo))
        def git(path, *args):
            return run_git_read(path, list(args)).stdout.strip()
        def require(condition, message):
            if not condition:
                raise RuntimeError(message)
        with lock_session(1200), state.lock(shared=True, phase='daemon-verify'), state.slot_lock(pool.repo_key, number, phase='daemon-verify'), pool.store_lock(shared=True, phase='daemon-verify'):
            require(git(pool.store, 'rev-parse', '--is-bare-repository') == 'true', 'source store is not bare')
            require(git(pool.store, 'rev-parse', '--is-shallow-repository') == 'false', 'source history is shallow')
            baseline = pool.cached_baseline_sha_read_only()
            require(baseline and history_coverage(pool.store, baseline)['complete'], 'baseline history is incomplete')
            outer = root / pool.slot_relative_path(number)
            source = outer / pool.module['path']
            require(not outer.is_symlink() and not source.is_symlink(), 'worktree is a symlink')
            require(source.resolve().is_relative_to(root), 'source worktree escaped workspace')
            require((outer / '.git').is_file() and (source / '.git').is_file(), 'slot contains a clone rather than linked worktrees')
            require(Path(git(outer, 'rev-parse', '--path-format=absolute', '--git-common-dir')).resolve() == state.common.resolve(), 'foreign outer object store')
            require(Path(git(source, 'rev-parse', '--path-format=absolute', '--git-common-dir')).resolve() == pool.store.resolve(), 'foreign source object store')
            validate_relative_worktree(root, outer, state.common)
            validate_relative_worktree(root, source, pool.store)
            for module in read_submodules(root, read_only=True):
                if module['path'] != pool.module['path']:
                    require(not (outer / module['path'] / '.git').exists(), 'unrelated submodule is realized')
            head = git(source, 'rev-parse', 'HEAD')
            output = {'repository': repo, 'slot': number, 'sourcePath': source.relative_to(root).as_posix(), 'sourceStore': pool.store.relative_to(state.common).as_posix(), 'checkoutSha': head}
            if request['pr'] is None:
                require(_pool_status(root, pool.module, number)['status'] == 'warm', 'slot is not warm')
            else:
                pr, admitted_head, admitted_base, admitted_merge = request['pr'], request['head'], request['base'], request['merge']
                manifest = json.loads(state.prepared_slot_path(pool.repo_key, number).read_text())
                require(manifest.get('schema') == 1 and manifest.get('repo') == repo and manifest.get('slot') == number and manifest.get('pr') == int(pr), 'prepared manifest identity mismatch')
                require(manifest.get('merge_sha') == head, 'prepared manifest does not match checkout')
                require(not admitted_merge or head == admitted_merge, 'admitted merge changed')
                parents = git(source, 'show', '-s', '--format=%P', head).split()
                require(parents == [admitted_base, admitted_head], 'merge parents do not match admitted target/source commits')
                merge_base = git(source, 'merge-base', admitted_base, admitted_head)
                require(history_coverage(pool.store, head)['complete'], 'prepared merge history is incomplete')
                output.update(sourceHeadSha=admitted_head, targetBaseSha=admitted_base, mergeBaseSha=merge_base, preparedManifest=state.prepared_slot_path(pool.repo_key, number).relative_to(root).as_posix())
            print(json.dumps(output))
        """;

    private sealed record WorktreeEvidence(
        string Repository,
        int Slot,
        string SourcePath,
        string SourceStore,
        string? CheckoutSha,
        string? MergeBaseSha,
        string? PreparedManifest
    );

    private sealed record HistoryEvidence(bool Complete);

    private sealed record StrictResetEvidence(
        [property: System.Text.Json.Serialization.JsonRequired] int Schema,
        [property: System.Text.Json.Serialization.JsonRequired] string Mode,
        [property: System.Text.Json.Serialization.JsonRequired] bool Strict,
        [property: System.Text.Json.Serialization.JsonRequired] string Status,
        [property: System.Text.Json.Serialization.JsonRequired] string Slot
    );

    private sealed record RootRepositoryEvidence(
        string Repository,
        string Path,
        string Branch,
        string TargetSha,
        string Status
    );

    private sealed record RootSetupEvidence(
        int Schema,
        string Mode,
        bool Complete,
        int ModuleCount,
        RootRepositoryEvidence[] Repositories
    );

    private sealed record RootInventory(int Schema, RootRepositoryEvidence[] Repositories);

    internal const string SlotPresenceScript = """
        import json, sys
        from pathlib import Path
        root = Path.cwd()
        path = root / sys.argv[1]
        for item in [path, *path.parents]:
            if item == root: break
            if item.is_symlink(): raise RuntimeError('Symlink slot refused')
        print(json.dumps({'absent': not path.exists()}))
        """;

    private sealed record SlotPresence([property: System.Text.Json.Serialization.JsonRequired] bool Absent);

    public async Task EnsureWarmAsync(ReviewSlot slot, CancellationToken ct)
    {
        slot.Validate();
        var probe = await RunAsync(["python3", "-c", SlotPresenceScript, slot.WorktreeRoot["/workspace/".Length..]], ct)
            .ConfigureAwait(false);
        RequireSuccess(probe, "Slot presence inspection");
        var presence =
            JsonSerializer.Deserialize<SlotPresence>(probe.Stdout, EvidenceJson)
            ?? throw new InvalidDataException("Slot presence evidence is missing.");
        if (presence.Absent)
        {
            await PrimeFullHistoryAsync(slot, ct).ConfigureAwait(false);
            await WarmAsync(slot, ct).ConfigureAwait(false);
        }
        else
            await ReadEvidenceAsync(slot, null, ct).ConfigureAwait(false);
    }

    internal const string RootSetupScript = ".claude/skills/repo-setup/scripts/repo_setup.py";

    public async Task SetupRootRepositoriesAsync(CancellationToken ct, TimeSpan? operationTimeout = null)
    {
        var installed = await _files
            .ReadFileAsync($"/workspace/{RootSetupScript}", SandboxReadLimits.RepositoryFileBytes, ct)
            .ConfigureAwait(false);
        if (!installed.Exists || installed.TooLarge)
            throw new InvalidOperationException(
                "The interactive operator must install repo-setup and its sibling review-setup helper."
            );
        var inventoryResult = await RunAsync(["python3", RootSetupScript, "inventory", "--root", "/workspace"], ct)
            .ConfigureAwait(false);
        RequireSuccess(inventoryResult, "Root repository inventory");
        var inventory = JsonSerializer.Deserialize<RootInventory>(inventoryResult.Stdout, EvidenceJson);
        if (
            inventory is not { Schema: 1, Repositories.Length: > 0 }
            || inventory.Repositories.Any(row =>
                row is null || string.IsNullOrWhiteSpace(row.Repository) || row.Path != $"repos/{row.Repository}"
            )
            || inventory.Repositories.Select(row => (row.Repository, row.Path)).Distinct().Count()
                != inventory.Repositories.Length
        )
            throw new InvalidDataException("Authoritative root repository inventory is missing or invalid.");
        var budget = operationTimeout ?? new CodeReviewDaemon.Sample.Configuration.SandboxLimits().CommandTimeout;
        if (budget < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        var result = await RunAsync(
                [
                    "python3",
                    RootSetupScript,
                    "apply",
                    "--root",
                    "/workspace",
                    "--timeout",
                    Math.Floor(budget.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                ],
                ct
            )
            .ConfigureAwait(false);
        if (result.ExitCode == 75)
            throw new RemoteWorkspaceOutcomeUnknownException(
                new IOException("Root setup could not prove Git descendant cleanup.")
            );
        RequireSuccess(result, "Root repository setup");
        var evidence = JsonSerializer.Deserialize<RootSetupEvidence>(result.Stdout, EvidenceJson);
        if (
            evidence is not { Schema: 1, Mode: "apply", Complete: true, ModuleCount: > 0, Repositories: not null }
            || evidence.Repositories.Any(row => row is null)
            || !inventory
                .Repositories.Select(row => (row.Repository, row.Path))
                .ToHashSet()
                .SetEquals(evidence.Repositories.Select(row => (row.Repository, row.Path)))
            || evidence.Repositories.Length != evidence.ModuleCount
            || evidence.Repositories.Select(row => row.Path).Distinct(StringComparer.Ordinal).Count()
                != evidence.ModuleCount
            || evidence.Repositories.Any(row =>
                string.IsNullOrWhiteSpace(row.Repository)
                || row.Path != $"repos/{row.Repository}"
                || row.Repository.Contains('/')
                || row.Repository.Contains('\\')
                || row.Repository is "." or ".."
                || string.IsNullOrWhiteSpace(row.Branch)
                || !IsSha(row.TargetSha)
                || row.Status != "ready"
            )
        )
            throw new InvalidDataException("Root setup did not prove complete repository preparation.");
    }

    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web);

    private readonly ISandboxCommandRunner _commands;
    private readonly ISandboxFileSystem _files;
    private readonly bool _autoDiscardCompletedSlotOnAdmission;

    public ReviewSetupScriptRunner(
        ISandboxCommandRunner commands,
        ISandboxFileSystem files,
        bool autoDiscardCompletedSlotOnAdmission = true
    )
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _autoDiscardCompletedSlotOnAdmission = autoDiscardCompletedSlotOnAdmission;
    }

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        foreach (
            var name in new[]
            {
                "review_pool_admin.py",
                "review_common.py",
                "review_reset.py",
                "review_setup.py",
                "review_git_prepare.py",
            }
        )
        {
            var script = await _files
                .ReadFileAsync(
                    $"/workspace/{ScriptsRelativePath}/{name}",
                    SandboxReadLimits.RepositoryFileBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!script.Exists || script.TooLarge)
                throw new InvalidOperationException(
                    "Review-setup assets are not installed or readable. The interactive LLM must install/maintain .claude/ and CLAUDE.md, then rerun setup."
                );
        }
    }

    public async Task PrimeFullHistoryAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        slot.Validate();
        var result = await RunAsync(
                [
                    "python3",
                    $"{ScriptsRelativePath}/review_pool_admin.py",
                    "prime-history",
                    "--repo",
                    slot.RepositoryName,
                    "--baseline-depth",
                    "1000000",
                    "--history-timeout",
                    "3600",
                    "--lock-timeout",
                    "1200",
                ],
                cancellationToken
            )
            .ConfigureAwait(false);
        RequireSuccess(result, "History priming");
        if (
            JsonSerializer.Deserialize<HistoryEvidence>(StripHistoryProgress(result.Stdout), EvidenceJson)?.Complete
            != true
        )
            throw new InvalidOperationException("History priming did not prove complete source ancestry.");
    }

    public async Task WarmAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        slot.Validate();
        RequireSuccess(
            await RunAsync(
                    [
                        "python3",
                        $"{ScriptsRelativePath}/review_pool_admin.py",
                        "warm",
                        "--repo",
                        slot.RepositoryName,
                        "--slot",
                        slot.Index.ToString(CultureInfo.InvariantCulture),
                        "--lock-timeout",
                        "1200",
                    ],
                    cancellationToken
                )
                .ConfigureAwait(false),
            "Slot warming"
        );
    }

    public async Task VerifyWarmAsync(IReadOnlyCollection<ReviewSlot> slots, CancellationToken cancellationToken)
    {
        var stores = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            var evidence = await ReadEvidenceAsync(slot, null, cancellationToken).ConfigureAwait(false);
            var store = evidence.SourceStore;
            if (stores.TryGetValue(slot.RepositoryName, out var expected) && expected != store)
                throw new InvalidDataException("Slots for one repository do not share their source object store.");
            stores[slot.RepositoryName] = store;
        }
    }

    public async Task<PreparedCheckout> PrepareAsync(
        ReviewRun run,
        ReviewSlot slot,
        RepoIdentity repository,
        CancellationToken cancellationToken
    )
    {
        slot.Validate();
        if (!string.Equals(slot.RepositoryName, repository.RepoName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Assigned slot does not belong to the admitted repository.");
        if (
            !IsSha(run.HeadSha)
            || !IsSha(run.BaseSha)
            || !int.TryParse(run.PrId, out var pr)
            || pr <= 0
            || (run.MergeSha is not null && !IsSha(run.MergeSha))
        )
            throw new InvalidDataException("Admitted PR commit identity is invalid.");
        if (!GitRemoteUrl.IsAzureDevOps(repository.Provider))
            throw new NotSupportedException(
                "Installed SourcePool scripts only parse ADO source modules; provider-neutral module support must be installed before reviewing this repository."
            );
        await EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
        if (_autoDiscardCompletedSlotOnAdmission)
            await DiscardCompletedSlotAsync(slot, cancellationToken).ConfigureAwait(false);
        // Debugging mode leaves occupied slots untouched and admits only a verified warm slot.
        _ = await ReadEvidenceAsync(slot, null, cancellationToken).ConfigureAwait(false);
        var setup = await RunAsync(
                [
                    "python3",
                    "-c",
                    SetupScript,
                    run.PrId,
                    "--repo",
                    slot.RepositoryName,
                    "--slot",
                    slot.Index.ToString(CultureInfo.InvariantCulture),
                    "--org",
                    $"https://dev.azure.com/{Uri.EscapeDataString(repository.OrgOrOwner)}",
                    "--transport",
                    "gateway",
                ],
                cancellationToken
            )
            .ConfigureAwait(false);
        if (setup.ExitCode == 3)
            throw new ReviewPullRequestMovedException("The PR moved during review setup; resolve the admission again.");
        if (setup.ExitCode is not (0 or 2))
            RequireSuccess(setup, "Review setup");
        var evidence = await ReadEvidenceAsync(slot, run, cancellationToken).ConfigureAwait(false);
        return new PreparedCheckout(
            slot.WorktreeRoot,
            slot.SourceRoot,
            $"{slot.WorktreeRoot}/PRs/{run.PrId}",
            $"review/{slot.RepositoryName}-{run.PrId}",
            MergeBaseOutcome.Resolved,
            evidence.MergeBaseSha,
            evidence.CheckoutSha,
            run.HeadSha,
            run.BaseSha,
            evidence.PreparedManifest,
            setup.ExitCode == 2
        );
    }

    internal async Task<System.Text.Json.Nodes.JsonObject> ResetReviewSlotAsync(
        System.Text.Json.Nodes.JsonObject identity,
        bool apply,
        CancellationToken ct
    )
    {
        var request = (System.Text.Json.Nodes.JsonObject)identity.DeepClone();
        request["apply"] = apply;
        var script = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, ".review", "scripts", "reset-review-slot.py"),
                ct
            )
            .ConfigureAwait(false);
        var result = await RunAsync(["python3", "-c", script, request.ToJsonString()], ct).ConfigureAwait(false);
        RequireSuccess(result, "Scoped review-slot reset");
        var receipt =
            System.Text.Json.Nodes.JsonNode.Parse(result.Stdout) as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidDataException("Reset receipt is missing.");
        if (
            receipt["schema"]?.GetValue<int>() != 1
            || !System.Text.Json.Nodes.JsonNode.DeepEquals(receipt["identity"], identity)
            || receipt["status"]?.GetValue<string>() is not ("inspected" or "reset")
            || (apply && receipt["status"]?.GetValue<string>() != "reset")
        )
            throw new InvalidDataException("Reset did not acknowledge the exact assignment.");
        return receipt;
    }

    private async Task DiscardCompletedSlotAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
                [
                    "python3",
                    $"{ScriptsRelativePath}/review_reset.py",
                    "--slot",
                    $"{slot.RepositoryName}-{slot.Index.ToString(CultureInfo.InvariantCulture)}",
                    "--mode",
                    "discard",
                    "--confirm",
                    "YES",
                    "--strict",
                    "--lock-timeout",
                    "1200",
                ],
                cancellationToken
            )
            .ConfigureAwait(false);
        RequireSuccess(result, "Strict review-slot reset");
        var receipt = JsonSerializer.Deserialize<StrictResetEvidence>(result.Stdout, EvidenceJson);
        if (
            receipt is not { Schema: 1, Mode: "discard", Strict: true, Status: "discarded" or "already_idle" }
            || receipt.Slot != $"{slot.RepositoryName}-{slot.Index.ToString(CultureInfo.InvariantCulture)}"
        )
            throw new InvalidDataException("Strict reset did not acknowledge the exact review slot.");
    }

    internal async Task VerifyPreparedAsync(ReviewSlot slot, ReviewRun run, CancellationToken ct) =>
        _ = await ReadEvidenceAsync(slot, run, ct).ConfigureAwait(false);

    private async Task<WorktreeEvidence> ReadEvidenceAsync(ReviewSlot slot, ReviewRun? run, CancellationToken ct)
    {
        slot.Validate();
        var result = await RunAsync(
                [
                    "python3",
                    "-c",
                    EvidenceScript,
                    JsonSerializer.Serialize(
                        new
                        {
                            repository = slot.RepositoryName,
                            slot = slot.Index,
                            pr = run?.PrId,
                            head = run?.HeadSha,
                            @base = run?.BaseSha,
                            merge = run?.MergeSha,
                        }
                    ),
                ],
                ct
            )
            .ConfigureAwait(false);
        RequireSuccess(result, "Worktree topology/provenance verification");
        var evidence =
            JsonSerializer.Deserialize<WorktreeEvidence>(result.Stdout, EvidenceJson)
            ?? throw new InvalidDataException("Worktree verification returned no evidence.");
        if (
            evidence.Repository != slot.RepositoryName
            || evidence.Slot != slot.Index
            || evidence.SourcePath != slot.SourceRelativePath
            || string.IsNullOrWhiteSpace(evidence.SourceStore)
        )
            throw new InvalidDataException("Worktree verification returned a different or incomplete slot identity.");
        if (
            run is not null
            && (
                !IsSha(evidence.CheckoutSha)
                || !IsSha(evidence.MergeBaseSha)
                || string.IsNullOrWhiteSpace(evidence.PreparedManifest)
            )
        )
            throw new InvalidDataException("Worktree verification returned incomplete commit evidence.");
        return evidence;
    }

    private async Task<SandboxCommandResult> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await _commands.RunAsync(new SandboxCommand(argv, "/workspace"), ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // No returned exit status means no proof of remote settlement, even when our wait was cancelled.
            throw new RemoteWorkspaceOutcomeUnknownException(exception);
        }
    }

    private static string StripHistoryProgress(string stdout)
    {
        // The installed CLI emits these two progress lines before its JSON result, even on cache hits.
        var json = stdout.Trim();
        if (!json.StartsWith("Fetch progress log: ", StringComparison.Ordinal))
            return json;
        var endOfLine = json.IndexOf('\n');
        if (endOfLine < 0 || string.IsNullOrWhiteSpace(json["Fetch progress log: ".Length..endOfLine]))
            throw new JsonException("History progress preamble is incomplete.");
        json = json[(endOfLine + 1)..].TrimStart();
        const string timeoutLine =
            "Effective aggregate history timeout: 3600s (--history-timeout); background tool timeout is separate.";
        endOfLine = json.IndexOf('\n');
        if (endOfLine < 0 || json[..endOfLine].TrimEnd() != timeoutLine)
            throw new JsonException("History progress preamble is not recognized.");
        return json[(endOfLine + 1)..].Trim();
    }

    private static bool IsAlreadyIdle(SandboxCommandResult result) =>
        result.ExitCode == 1
        && string.Equals(
            result.Stderr.Trim(),
            "Review reset stopped: No managed pooled run exists for that repository slot.",
            StringComparison.Ordinal
        );

    private static void RequireSuccess(SandboxCommandResult result, string operation)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"{operation} failed with exit {result.ExitCode}.");
    }

    private static bool IsSha(string? value) => value?.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}

internal sealed class ReviewPullRequestMovedException(string message) : InvalidOperationException(message);

internal sealed class RemoteWorkspaceOutcomeUnknownException(Exception innerException)
    : InvalidOperationException(
        "A Gateway command outcome is unknown; the shared review workspace must remain quarantined.",
        innerException
    );
