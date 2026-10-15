using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Host-only, one-push archival of the specifically classified Nova review.</summary>
internal static class ArchiveMergedPrCommand
{
    private const string Branch = "review/nova-5608424";
    private const string Prefix = "PRs/nova-5608424/";
    private const string NoticePath = Prefix + "archive-notice.md";
    private const string OriginalTip = "f2e25cefd902200c8daa894954888822c4037de8";
    private const string OriginalThread = "thread-1788341202819-0281375a949d4f7fa39fafb368fe175d";
    private const string RepoKey = "azure-devops/o365exchange/weve_da/nova";
    private const string IntentKind = "merged-archive-intent";
    private const string CompletionKind = "merged-archive-completion";
    private const string Operation = "merged_archive_main";

    public static async Task<JsonObject> RunAsync(
        ScopedCommandLineResult command,
        CodeReviewDaemonOptions options,
        GitRunner git,
        string repoRoot,
        string databasePath,
        IReadOnlyList<IPrProvider> providers,
        ILoggerFactory loggerFactory,
        Func<ReviewStore> openStore,
        HostGitPushAuthorization pushAuthorization,
        CancellationToken ct
    )
    {
        if (
            options.EnablePrPolling
            || options.EnableCommentPosting
            || options.EnableGitPush
            || options.AutoDiscardCompletedSlotOnAdmission
            || command.RunPrRepoKey != "O365Exchange/Weve_DA/Nova"
            || command.RunPrId != "5608424"
            || command.ArtifactTipSha != OriginalTip
            || string.IsNullOrWhiteSpace(command.ArchiveEvidenceRoot)
        )
            throw new InvalidDataException(
                "The explicit qualified archive is limited to Nova #5608424 with broad writes disabled."
            );
        var targets = PrPollTargetBuilder.Build(options, loggerFactory.CreateLogger("ArchiveMergedPrCommand"));
        var target = targets.Single(value => value.Repo.NormalizedKey == RepoKey);
        if (
            targets
                .Select(value => value.Repo)
                .DistinctBy(repo => repo.NormalizedKey)
                .Count(repo => ReviewBranchManager.RepoSlug(repo) == ReviewBranchManager.RepoSlug(target.Repo)) != 1
        )
            throw new InvalidDataException("The archive branch name is ambiguous across configured repositories.");
        var provider = providers.Single(value => value.Provider == target.Provider);
        var source = await provider.GetPullRequestAsync(target.Repo, command.RunPrId!, ct);
        if (
            source is null
            || source.PrId != command.RunPrId
            || source.LifecycleState != PrLifecycleState.Merged
            || source.MergeCommitSha != command.LandedSha
            || string.IsNullOrWhiteSpace(source.TargetRefName)
        )
            throw new InvalidDataException("The exact merged source PR is unavailable.");
        var original = await ReadOriginalAsync(git, repoRoot, ct);
        var directory = Path.GetFullPath(command.ArchiveEvidenceRoot!);
        var qualifiedRoot = Path.GetFullPath(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "repos/LmDotnetTools/samples/LmStreaming.Sample/bin/Debug/net9.0/conversations"
            )
        );
        if (
            !string.Equals(directory, qualifiedRoot, StringComparison.Ordinal)
            || !Directory.Exists(directory)
            || Directory.Exists(Path.Combine(directory, OriginalThread))
            || File.Exists(Path.Combine(directory, OriginalThread))
        )
            throw new InvalidOperationException("The approved local conversation absence is no longer established.");
        var notice = BuildNotice();
        ReviewArtifactExport.ValidateNoSecrets(new ReviewArtifactFile(NoticePath, notice));
        if (notice.Length > 4096)
            throw new InvalidOperationException("The archive notice exceeds the fixed bound.");
        var noticeSha = Hash(notice);
        var noticeBlobId = GitBlobId(notice);

        await using var repositoryLock = await HostRetentionWorkspace.AcquireRepositoryLockAsync(repoRoot, ct);
        await ReviewSlotPreparer.VerifyStoreOriginAsync(git, repoRoot, options.ResolvedStoreUrl!, ct);
        var remoteBranch = await ReadHeadAsync(git, repoRoot, Branch, ct);
        var remoteMain = await ReadHeadAsync(git, repoRoot, "main", ct);
        if (remoteMain is null || remoteBranch is not (null or OriginalTip))
            throw new InvalidOperationException("Remote main is unavailable or the original artifact branch changed.");

        // An absent branch can only replay an already recorded archive. Reject before opening
        // ReviewStore, whose constructor opens and migrates writable SQLite.
        if (remoteBranch is null && !HasArchiveReceipt(databasePath, target.Repo.NormalizedKey, command.RunPrId!))
            throw new InvalidOperationException("Original archive branch is absent without a prior archive receipt.");
        if (remoteBranch == OriginalTip)
            await RequireCleanMainAsync(git, repoRoot, remoteMain, ct);
        var store = openStore();
        var repoId = store.EnsureRepo(target.Repo);
        var previous = store.GetLatestReviewRun(repoId, source.PrId);
        if (previous is not null && previous.ReviewKind != "merged-archive")
            throw new InvalidOperationException("A prior run for this PR requires separate reconciliation.");
        var seed = new ReviewRun
        {
            RepoId = repoId,
            PrId = source.PrId,
            HeadSha = source.HeadSha,
            BaseSha = source.BaseSha,
            MergeSha = source.MergeCommitSha,
            TriggerWatermark = $"archive:{OriginalTip}:{command.LandedSha}:{command.ParentSha}",
            ReviewKind = "merged-archive",
            VariantId = OriginalTip,
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Pending,
            PrLifecycleState = PrLifecycleState.Merged,
        };
        var run = store.FindReviewRunByIdentity(seed) ?? store.CreateOrGetReviewRun(seed);
        if (
            run.ParkedAt is not null
            || run.ReviewKind != seed.ReviewKind
            || run.TriggerWatermark != seed.TriggerWatermark
            || run.Mode != seed.Mode
            || run.MergeSha != seed.MergeSha
        )
            throw new InvalidOperationException("Archive run identity differs from the original admission.");
        var intent = new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["Repository"] = RepoKey,
            ["PrId"] = run.PrId,
            ["OriginalTip"] = OriginalTip,
            ["OriginalManifest"] = original.Manifest.DeepClone(),
            ["OriginalManifestSha256"] = Hash(original.Manifest.ToJsonString()),
            ["SourceHead"] = run.HeadSha,
            ["SourceBase"] = run.BaseSha,
            ["LandedSha"] = command.LandedSha,
            ["ParentSha"] = command.ParentSha,
            ["ParentVerified"] = false,
            ["ConversationId"] = OriginalThread,
            ["Availability"] = "absent-from-inspected-local-output; live-and-historical-host-unverified",
            ["NoticeSha256"] = noticeSha,
        };
        var earlier = store.TryGetLatestArtifact(run.Id, IntentKind);
        if (earlier is not null && earlier.Payload != intent.ToJsonString())
            throw new InvalidOperationException("Archive intent differs from the durable original.");
        if (earlier is null)
            AddArtifact(store, run.Id, IntentKind, intent);
        var receipt = store.EnqueueOutbox(
            new OutboxEntry
            {
                IdempotencyKey = $"archive:nova:5608424:{OriginalTip}",
                Provider = RepoIdentity.ToPublisherNamespace(target.Repo.Provider),
                ReviewRunId = run.Id,
                Operation = Operation,
                ArtifactKind = "archive-only",
                Status = OutboxStatus.Pending,
                BodyHash = Hash(intent.ToJsonString()),
            }
        );
        if (receipt.ReviewRunId != run.Id || receipt.BodyHash != Hash(intent.ToJsonString()))
            throw new InvalidOperationException("Archive outbox identity changed.");
        var candidate = store.TryGetLatestArtifact(run.Id, "merged-archive-candidate");
        string mainSha;
        if (receipt.Status == OutboxStatus.Posted)
        {
            mainSha =
                receipt.ProviderResponseId ?? throw new InvalidOperationException("Archive receipt has no main SHA.");
            if (candidate is null || JsonNode.Parse(candidate.Payload)?["MainSha"]?.GetValue<string>() != mainSha)
                throw new InvalidOperationException("Archive candidate receipt is missing or differs.");
        }
        else if (receipt.Status == OutboxStatus.Sending)
        {
            mainSha = candidate is null
                ? throw new InvalidOperationException("Archive push is uncertain; candidate evidence is missing.")
                : JsonNode.Parse(candidate.Payload)?["MainSha"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Archive candidate SHA is missing.");
            if (!string.Equals(remoteMain, mainSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Archive push is uncertain; remote main did not prove its exact candidate."
                );
            await VerifyRemoteContentAsync(git, repoRoot, remoteMain, original, noticeBlobId, ct);
            if (!store.TryTransitionOutbox(receipt.Id, OutboxStatus.Sending, OutboxStatus.Posted, mainSha))
                throw new InvalidOperationException("Archive receipt reconciliation failed.");
        }
        else if (receipt.Status == OutboxStatus.Pending)
        {
            if (candidate is not null || remoteBranch != OriginalTip)
                throw new InvalidOperationException(
                    "Archive admission has an uncertain candidate or missing original branch."
                );
            mainSha = await BuildCandidateAsync(git, repoRoot, remoteMain, original, notice, ct);
            var candidateArtifact = new JsonObject
            {
                ["SchemaVersion"] = 1,
                ["PriorMainSha"] = remoteMain,
                ["MainSha"] = mainSha,
                ["IntentSha256"] = Hash(intent.ToJsonString()),
            };
            AddArtifact(store, run.Id, "merged-archive-candidate", candidateArtifact);
            if (!store.TryTransitionOutbox(receipt.Id, OutboxStatus.Pending, OutboxStatus.Sending))
                throw new InvalidOperationException("Archive push ownership changed.");
            if (
                await ReadHeadAsync(git, repoRoot, "main", ct) != remoteMain
                || await ReadHeadAsync(git, repoRoot, Branch, ct) != OriginalTip
            )
                throw new InvalidOperationException("Remote refs changed before archive push.");
            using var pushScope = pushAuthorization.EnterScope();
            pushAuthorization.AuthorizePush("main");
            await GitAsync(git, repoRoot, ct, "push", "origin", "main");
            if (await ReadHeadAsync(git, repoRoot, "main", ct) != mainSha)
                throw new InvalidOperationException("Remote main did not confirm the archive candidate.");
            await VerifyRemoteContentAsync(git, repoRoot, mainSha, original, noticeBlobId, ct);
            if (!store.TryTransitionOutbox(receipt.Id, OutboxStatus.Sending, OutboxStatus.Posted, mainSha))
                throw new InvalidOperationException("Archive receipt acknowledgement failed.");
        }
        else
            throw new InvalidOperationException("Archive outbox is in an unsupported state.");

        await VerifyRemoteContentAsync(git, repoRoot, mainSha, original, noticeBlobId, ct);
        var currentMain = await ReadHeadAsync(git, repoRoot, "main", ct);
        if (currentMain != mainSha)
            throw new InvalidOperationException(
                "Remote main moved after archive proof; preserve branch for reconciliation."
            );
        if (remoteBranch == OriginalTip)
        {
            using var deleteScope = pushAuthorization.EnterScope();
            pushAuthorization.AuthorizeGuardedDelete(Branch, OriginalTip);
            var deletion = await git.RunAsync(
                [
                    "push",
                    ReviewBranchManager.DeleteExpectedShaArgument(Branch, OriginalTip),
                    "origin",
                    "--delete",
                    Branch,
                ],
                repoRoot,
                ct
            );
            var remaining = await ReadHeadAsync(git, repoRoot, Branch, ct);
            if (!deletion.Succeeded || remaining is not null)
                throw new InvalidOperationException("Original artifact branch deletion is unresolved.");
            // Preserve the local ref. It may contain unpushed work unrelated to the archived tip.
        }
        else if (remoteBranch is not null)
            throw new InvalidOperationException("The original branch changed after archive import.");
        if (await ReadHeadAsync(git, repoRoot, Branch, ct) is not null)
            throw new InvalidOperationException("Original artifact branch remains present.");
        await VerifyRemoteContentAsync(git, repoRoot, mainSha, original, noticeBlobId, ct);
        var completion = new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["Disposition"] = "ArchivedWithoutAnalysis",
            ["IntentSha256"] = Hash(intent.ToJsonString()),
            ["MainSha"] = mainSha,
            ["OriginalTip"] = OriginalTip,
            ["OriginalFileCount"] = original.Paths.Count,
            ["KnowledgeEntryCount"] = 0,
        };
        var priorCompletion = store.TryGetLatestArtifact(run.Id, CompletionKind);
        if (priorCompletion is not null && priorCompletion.Payload != completion.ToJsonString())
            throw new InvalidOperationException("Archive completion receipt differs.");
        if (priorCompletion is null)
            AddArtifact(store, run.Id, CompletionKind, completion);
        if (run.WorkflowStatus != WorkflowStatus.Completed)
            store.UpdateReviewRunState(run.Id, ReviewStage.Posted, WorkflowStatus.Completed, PrLifecycleState.Merged);
        return new JsonObject
        {
            ["RunId"] = run.Id,
            ["Outcome"] = "ArchivedWithoutAnalysis",
            ["MainSha"] = mainSha,
        };
    }

    private static bool HasArchiveReceipt(string databasePath, string repoKey, string prId)
    {
        if (!File.Exists(databasePath))
            return false;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM review_run rr
                JOIN repo r ON r.id = rr.repo_id
                JOIN review_outbox o ON o.review_run_id = rr.id
                WHERE r.normalized_key = $repo AND rr.pr_id = $pr AND rr.review_kind = 'merged-archive'
                  AND rr.variant_id = $tip AND o.idempotency_key = $key
                  AND o.operation = $operation AND o.status = 'Posted'
            );
            """;
        command.Parameters.AddWithValue("$repo", repoKey);
        command.Parameters.AddWithValue("$pr", prId);
        command.Parameters.AddWithValue("$tip", OriginalTip);
        command.Parameters.AddWithValue("$key", $"archive:nova:5608424:{OriginalTip}");
        command.Parameters.AddWithValue("$operation", Operation);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task RequireCleanMainAsync(GitRunner git, string root, string remoteMain, CancellationToken ct)
    {
        if (
            await GitAsync(git, root, ct, "status", "--porcelain=v1", "--untracked-files=all") != ""
            || await GitAsync(git, root, ct, "symbolic-ref", "--short", "HEAD") != "main"
            || await GitAsync(git, root, ct, "rev-parse", "HEAD") != remoteMain
            || await ReadHeadAsync(git, root, Branch, ct) != OriginalTip
        )
            throw new InvalidOperationException("Archive requires a clean main checkout at the admitted remote SHA.");
    }

    private static async Task<string> BuildCandidateAsync(
        GitRunner git,
        string root,
        string previousMain,
        OriginalFiles original,
        string notice,
        CancellationToken ct
    )
    {
        await GitAsync(git, root, ct, "fetch", "origin", "refs/heads/main", "refs/heads/" + Branch);
        if (await GitAsync(git, root, ct, "rev-parse", "refs/remotes/origin/main") != previousMain)
            throw new InvalidOperationException("Fetched main differs from remote admission.");
        await RequireCleanMainAsync(git, root, previousMain, ct);
        foreach (var (path, entry) in original.Paths)
        {
            var existing = await ReadEntryAsync(git, root, previousMain, path, ct);
            if (existing is not null && existing != entry)
                throw new InvalidOperationException("Main contains a conflicting original review file.");
            await RequireDirectoryAncestorsAsync(git, root, previousMain, path, ct);
        }
        if (await ReadEntryAsync(git, root, previousMain, NoticePath, ct) is not null)
            throw new InvalidOperationException("Main already contains an archive notice.");
        await RequireDirectoryAncestorsAsync(git, root, previousMain, NoticePath, ct);
        var reviewDirectory = Path.Combine(root, "PRs", "nova-5608424");
        foreach (var ancestor in new[] { root, Path.Combine(root, "PRs") })
            if (HostPathGuard.Check(ancestor) is not null || !Directory.Exists(ancestor))
                throw new InvalidOperationException("An archive destination directory is missing or redirected.");
        if (
            HostPathGuard.Check(reviewDirectory) is not null
            || Directory.Exists(reviewDirectory)
            || File.Exists(reviewDirectory)
        )
            throw new InvalidOperationException("Archive destination already exists on disk.");
        await GitAsync(
            git,
            root,
            ct,
            ["restore", $"--source={OriginalTip}", "--staged", "--worktree", "--", .. original.Paths.Keys]
        );
        var noticeFile = Path.Combine(root, "PRs", "nova-5608424", "archive-notice.md");
        foreach (var ancestor in new[] { root, Path.Combine(root, "PRs"), reviewDirectory })
            if (HostPathGuard.Check(ancestor) is not null || !Directory.Exists(ancestor))
                throw new InvalidOperationException("An archive destination directory is missing or redirected.");
        await using (
            var output = new FileStream(
                noticeFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous
            )
        )
        await using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            await writer.WriteAsync(notice.AsMemory(), ct);
        await GitAsync(git, root, ct, "add", "--", NoticePath);
        var allowed = original.Paths.Keys.Append(NoticePath).ToHashSet(StringComparer.Ordinal);
        var changed = await GitAsync(git, root, ct, "diff", "--cached", "--name-status", "--no-renames");
        foreach (var line in changed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 2 || parts[0] != "A" || !allowed.Contains(parts[1]))
                throw new InvalidOperationException("Archive staged a file outside positive additions.");
        }
        if (changed.Length == 0)
            throw new InvalidOperationException("Archive candidate contains no additions.");
        var status = await GitAsync(git, root, ct, "status", "--porcelain=v1", "--untracked-files=all");
        if (
            status
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(row => row.Length < 4 || row[1] != ' ' || !allowed.Contains(row[3..].TrimEnd('\r')))
        )
            throw new InvalidOperationException("Archive changed an unexpected path.");
        await GitAsync(
            git,
            root,
            ct,
            "commit",
            "-m",
            "Archive original Nova review #5608424 without completion analysis"
        );
        var candidate = await GitAsync(git, root, ct, "rev-parse", "HEAD");
        if (
            await GitAsync(git, root, ct, "diff", "--name-only", previousMain, candidate) is { } changedPaths
            && changedPaths
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(p => !allowed.Contains(p.TrimEnd('\r')))
        )
            throw new InvalidOperationException("Archive candidate changed paths outside positive imports.");
        await VerifyLocalContentAsync(git, root, candidate, original, GitBlobId(notice), ct);
        return candidate;
    }

    private static async Task<OriginalFiles> ReadOriginalAsync(GitRunner git, string root, CancellationToken ct)
    {
        var result = await git.RunAsync(
            ["ls-tree", "-r", "-l", "-z", "--full-tree", OriginalTip, "--", Prefix],
            root,
            ct
        );
        if (!result.Succeeded)
            throw new InvalidDataException("Original review tip is unavailable in the retention checkout.");
        var entries = result.Stdout.TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length is 0 or > 64)
            throw new InvalidDataException("Original review file count is outside the archive bound.");
        var paths = new SortedDictionary<string, (string Mode, string Sha)>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var entry in entries)
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0)
                throw new InvalidDataException("Original review tree is malformed.");
            var header = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = entry[(tab + 1)..];
            if (
                header.Length != 4
                || header[0] is not ("100644" or "100755")
                || header[1] != "blob"
                || header[2].Length != 40
                || !header[2].All(Uri.IsHexDigit)
                || !long.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || size is < 0 or > 262144
                || !path.StartsWith(Prefix, StringComparison.Ordinal)
                || path.Length > 512
                || path == NoticePath
                || path.Split('/')
                    .Any(segment =>
                        segment.Length == 0
                        || segment is "." or ".." or ".git"
                        || !segment.All(character =>
                            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                        )
                    )
                || !(path.EndsWith(".md", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal))
                || !paths.TryAdd(path, (header[0], header[2]))
            )
                throw new InvalidDataException("Original review tree contains an unsupported file.");
            bytes += size;
        }
        if (paths.Count != 13 || bytes != 106402 || !paths.ContainsKey(Prefix + "review.md") || bytes > 1048576)
            throw new InvalidDataException("Original review files no longer match the classified archive.");
        var context = await GitAsync(git, root, ct, "show", OriginalTip + ":" + Prefix + "PR_Context_01.md");
        if (!context.Contains("| Hosted conversation | `" + OriginalThread + "` |", StringComparison.Ordinal))
            throw new InvalidDataException("Original hosted conversation reference changed.");
        var manifest = new JsonArray([
            .. paths.Select(item => new JsonObject
            {
                ["Path"] = item.Key,
                ["Mode"] = item.Value.Mode,
                ["Sha"] = item.Value.Sha,
            }),
        ]);
        return new OriginalFiles(paths, manifest);
    }

    private static string BuildNotice() =>
        "# Conversation unavailable\n\n"
        + "The original hosted conversation is absent from one inspected local LmStreaming.Sample output directory. "
        + "That directory's relationship to the configured live host and historical review host was not verified. "
        + "Live availability was not verified; this does not establish deletion.\n\n"
        + "Nova PR #5608424 retains its original review files. "
        + $"Original tip: {OriginalTip}. Hosted conversation: {OriginalThread}.\n\n"
        + "No completion analysis, judgment or knowledge extraction was performed.\n";

    private static async Task VerifyRemoteContentAsync(
        GitRunner git,
        string root,
        string remoteMain,
        OriginalFiles original,
        string noticeBlobId,
        CancellationToken ct
    )
    {
        if (await ReadHeadAsync(git, root, "main", ct) != remoteMain)
            throw new InvalidOperationException("Remote main changed during archive proof.");
        await GitAsync(git, root, ct, "fetch", "origin", "refs/heads/main");
        if (await GitAsync(git, root, ct, "rev-parse", "refs/remotes/origin/main") != remoteMain)
            throw new InvalidOperationException("Fetched main differs from archive proof.");
        await VerifyLocalContentAsync(git, root, remoteMain, original, noticeBlobId, ct);
    }

    private static async Task VerifyLocalContentAsync(
        GitRunner git,
        string root,
        string tree,
        OriginalFiles original,
        string noticeBlobId,
        CancellationToken ct
    )
    {
        foreach (var (path, entry) in original.Paths)
            if (await ReadEntryAsync(git, root, tree, path, ct) != entry)
                throw new InvalidOperationException("An original review blob or file mode is missing from main.");
        if (await ReadEntryAsync(git, root, tree, NoticePath, ct) is not { Mode: "100644", Sha: var noticeBlob })
            throw new InvalidOperationException("Archive notice is missing or is not a regular file.");
        if (!string.Equals(noticeBlob, noticeBlobId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Archive notice content differs from the approved text.");
        var originalLinks = await GitAsync(git, root, ct, "ls-tree", "-r", OriginalTip, "--", "repos/");
        var currentLinks = await GitAsync(git, root, ct, "ls-tree", "-r", tree, "--", "repos/");
        if (originalLinks != currentLinks)
            throw new InvalidOperationException("Source gitlinks differ from the original review branch.");
    }

    private static async Task RequireDirectoryAncestorsAsync(
        GitRunner git,
        string root,
        string tree,
        string path,
        CancellationToken ct
    )
    {
        var parent = path.LastIndexOf('/');
        while (parent >= 0)
        {
            var ancestor = path[..parent];
            var result = await git.RunAsync(["ls-tree", "-z", "--full-tree", tree, "--", ancestor], root, ct);
            if (
                !result.Succeeded
                || (result.Stdout.Length > 0 && !result.Stdout.StartsWith("040000 tree ", StringComparison.Ordinal))
            )
                throw new InvalidOperationException("An archive destination crosses a non-directory object.");
            parent = ancestor.LastIndexOf('/');
        }
    }

    private static async Task<(string Mode, string Sha)?> ReadEntryAsync(
        GitRunner git,
        string root,
        string tree,
        string path,
        CancellationToken ct
    )
    {
        var result = await git.RunAsync(["ls-tree", "-z", "--full-tree", tree, "--", path], root, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException("A selected Git object cannot be read.");
        var items = result.Stdout.TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (items.Length == 0)
            return null;
        if (items.Length != 1 || !items[0].EndsWith("\t" + path, StringComparison.Ordinal))
            throw new InvalidOperationException("A selected Git path resolved ambiguously.");
        var header = items[0][..items[0].IndexOf('\t')].Split(' ');
        return header.Length == 3 && header[0] is "100644" or "100755" && header[1] == "blob"
            ? (header[0], header[2])
            : throw new InvalidOperationException("A selected Git object is not a regular file.");
    }

    private static async Task<string?> ReadHeadAsync(GitRunner git, string root, string branch, CancellationToken ct)
    {
        var result = await git.RunAsync(
            ["ls-remote", "--exit-code", "--heads", "origin", "refs/heads/" + branch],
            root,
            ct
        );
        if (result.ExitCode == 2)
            return null;
        if (!result.Succeeded)
            throw new InvalidOperationException("Remote branch state is unknown.");
        var fields = result.Stdout.Trim().Split('\t');
        return
            fields.Length == 2
            && fields[0].Length == 40
            && fields[0].All(Uri.IsHexDigit)
            && fields[1] == "refs/heads/" + branch
            ? fields[0]
            : throw new InvalidOperationException("Remote branch response is malformed.");
    }

    private static async Task<string> GitAsync(
        GitRunner git,
        string root,
        CancellationToken ct,
        params string[] args
    ) => await GitAsync(git, root, ct, (IReadOnlyList<string>)args);

    private static async Task<string> GitAsync(
        GitRunner git,
        string root,
        CancellationToken ct,
        IReadOnlyList<string> args
    )
    {
        var result = await git.RunAsync(args, root, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Archive Git operation did not complete; preserve state for reconciliation."
            );
        return result.Stdout.Trim();
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string GitBlobId(string text)
    {
        var contents = Encoding.UTF8.GetBytes(text);
        var header = Encoding.ASCII.GetBytes($"blob {contents.Length}\0");
        var blob = new byte[header.Length + contents.Length];
        header.CopyTo(blob, 0);
        contents.CopyTo(blob, header.Length);
        return Convert.ToHexString(SHA1.HashData(blob)).ToLowerInvariant();
    }

    private static void AddArtifact(ReviewStore store, long runId, string kind, JsonObject payload) =>
        _ = store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = runId,
                ArtifactSchemaVersion = 1,
                ArtifactKind = kind,
                Provider = "archive-only",
                Payload = payload.ToJsonString(),
            }
        );

    private sealed record OriginalFiles(SortedDictionary<string, (string Mode, string Sha)> Paths, JsonArray Manifest);
}
