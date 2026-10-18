using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Reviewd.Sample;

internal static class PrCommands
{
    public static async Task<int> RunAsync(CliOptions options, ReviewdConfig config, CancellationToken ct)
    {
        _ = ParseRepos(options);
        if (options.Command == "pr pick")
        {
            return await PrSelection.RunAsync(options, ct);
        }
        if (options.Command == "pr bind-thread")
        {
            var repo = ParseRepo(options.Required("repo"));
            await PrThreadBindings.SetAsync(
                options.Get("db") ?? "reviewd.db",
                repo,
                options.Required("pr"),
                options.Required("thread"),
                ct
            );
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        repository = repo.DisplayName,
                        prId = options.Required("pr"),
                        threadId = options.Required("thread"),
                    }
                )
            );
            return 0;
        }
        var auth =
            config.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
            ?? throw new InvalidOperationException("Configure Auth and the existing Auth:TokenStoreDir.");
        if (string.IsNullOrWhiteSpace(auth.TokenStoreDir) || !Path.IsPathFullyQualified(auth.TokenStoreDir))
        {
            throw new InvalidOperationException(
                "Auth:TokenStoreDir must explicitly name the absolute shared OAuth cache directory."
            );
        }
        var repos = ParseRepos(options);
        var maxPages = config.Configuration.GetValue(
            "CodeReviewDaemon:MaxPagesPerPoll",
            CodeReviewDaemonOptions.DefaultMaxPagesPerPoll
        );
        var maxPrs = config.Configuration.GetValue(
            "CodeReviewDaemon:MaxPrsPerPage",
            CodeReviewDaemonOptions.DefaultMaxPrsPerPage
        );
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        IPrProvider? ado = null;
        IPrProvider? github = null;
        AdoPrActivityReader? adoActivity = null;
        if (repos.Any(repo => repo.Provider == "azure-devops"))
        {
            if (string.IsNullOrWhiteSpace(auth.Ado.ClientId) || auth.Ado.Scopes.Length == 0)
            {
                throw new InvalidOperationException(
                    "Configure Auth:Ado:ClientId and Auth:Ado:Scopes to match the signed-in host."
                );
            }
            var tokenCache = Path.Combine(auth.TokenStoreDir, "msal-ado.bin");
            if (!File.Exists(tokenCache))
            {
                throw new InvalidOperationException(
                    "The shared ADO OAuth cache is missing. Sign in through the existing host first."
                );
            }
            var tokens = new AdoOAuthProvider(auth.Ado, tokenCache, NullLogger<AdoOAuthProvider>.Instance);
            ado = new AdoPrProvider(http, tokens, NullLogger<AdoPrProvider>.Instance, maxPages, maxPrs);
            adoActivity = new AdoPrActivityReader(http, tokens);
        }
        if (repos.Any(repo => repo.Provider == "github"))
        {
            // Listing reads the signed-in token from the shared store; it never runs the OAuth sign-in flow.
            var tokens = new GitHubOAuthProvider(
                auth.Github,
                new FileOAuthTokenStore(auth.TokenStoreDir, NullLogger<FileOAuthTokenStore>.Instance),
                http,
                NullLogger<GitHubOAuthProvider>.Instance
            );
            github = new GitHubPrProvider(http, tokens, NullLogger<GitHubPrProvider>.Instance, maxPages, maxPrs);
        }
        return await RunAsync(
            options,
            repo => repo.Provider == "github" ? github! : ado!,
            ct,
            // GitHub's updated_at already covers pushes and comments, so only ADO needs the activity read.
            (repo, pr, token) =>
                repo.Provider == "azure-devops"
                    ? adoActivity!.ReadAsync(repo, pr, token)
                    : Task.FromResult(new AdoPrActivityReader.Activity(null, null))
        );
    }

    internal static Task<int> RunAsync(
        CliOptions options,
        IPrProvider provider,
        CancellationToken ct,
        Func<RepoIdentity, string, CancellationToken, Task<AdoPrActivityReader.Activity>>? readActivity = null
    ) => RunAsync(options, _ => provider, ct, readActivity);

    private static async Task<int> RunAsync(
        CliOptions options,
        Func<RepoIdentity, IPrProvider> providerFor,
        CancellationToken ct,
        Func<RepoIdentity, string, CancellationToken, Task<AdoPrActivityReader.Activity>>? readActivity
    )
    {
        var repos = ParseRepos(options);
        var exit = 0;
        foreach (var repo in repos)
        {
            try
            {
                await CheckRepoAsync(options, providerFor(repo), repo, readActivity, ct);
            }
            catch (Exception ex) when (repos.Count > 1 && !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"{repo.DisplayName}: {ex.Message}; this repository's snapshot was not saved. Other repositories may have been saved."
                );
                exit = 1;
            }
        }
        return exit;
    }

    private static async Task CheckRepoAsync(
        CliOptions options,
        IPrProvider provider,
        RepoIdentity repo,
        Func<RepoIdentity, string, CancellationToken, Task<AdoPrActivityReader.Activity>>? readActivity,
        CancellationToken ct
    )
    {
        var page = await provider.ListOpenPullRequestsAsync(
            new PrPollRequest { Repo = repo, Scope = $"{repo.NormalizedKey}:active-prs" },
            ct
        );
        if (!page.IsComplete)
        {
            throw new InvalidOperationException(
                "PR listing is incomplete. Increase CodeReviewDaemon:MaxPagesPerPoll or MaxPrsPerPage; no snapshot was saved."
            );
        }
        var now = DateTimeOffset.UtcNow;
        var active = new Dictionary<string, PrObservation>(StringComparer.Ordinal);
        foreach (var pr in page.PullRequests)
        {
            var activity = readActivity is null ? null : await readActivity(repo, pr.PrId, ct);
            active.Add(
                pr.PrId,
                new PrObservation(pr.PrId, MapLifecycle(pr.LifecycleState), pr.HeadSha, pr.Title)
                {
                    ObservedAt = now,
                    IsDraft = pr.IsDraft,
                    CreatedAt = pr.CreatedAt,
                    SourceUpdatedAt = activity?.SourceUpdatedAt ?? pr.UpdatedAt,
                    LastCommentPostedAt = activity?.LastCommentPostedAt,
                }
            );
        }
        var database = options.Get("db") ?? "reviewd.db";
        if (options.Command == "pr list")
        {
            foreach (var pr in active.Values.OrderBy(pr => pr.PrId, StringComparer.Ordinal))
            {
                var thread = File.Exists(database)
                    ? await PrThreadBindings.GetAsync(database, repo, pr.PrId, ct)
                    : null;
                Console.WriteLine(
                    JsonSerializer.Serialize(
                        new
                        {
                            repository = repo.DisplayName,
                            current = pr,
                            threadId = thread,
                        }
                    )
                );
            }
            Console.WriteLine($"Listed {active.Count} active PR(s) in {repo.DisplayName} (including drafts).");
            return;
        }
        // Each repository is a single upsert, after every required remote read succeeds.
        using var connection = PrThreadBindings.Connection(database);
        var store = new SqliteKvStore(connection);
        var key = $"reviewd/pr-snapshot/{repo.NormalizedKey}";
        var previous =
            await store.GetAsync<Dictionary<string, PrObservation>>(key, ct)
            ?? new Dictionary<string, PrObservation>(StringComparer.Ordinal);
        var current = new Dictionary<string, PrObservation>(previous, StringComparer.Ordinal);
        foreach (var pr in active.Values)
        {
            previous.TryGetValue(pr.PrId, out var old);
            current[pr.PrId] = pr with
            {
                LastChangedAt = old is null || Changed(old, pr) ? now : old.LastChangedAt ?? now,
            };
        }
        foreach (
            var pr in previous.Values.Where(pr => pr.Lifecycle == PrLifecycle.Open && !active.ContainsKey(pr.PrId))
        )
        {
            var lifecycle = await provider.GetPrStateAsync(repo, pr.PrId, ct);
            var head = await provider.GetCurrentHeadShaAsync(repo, pr.PrId, ct);
            current[pr.PrId] = pr with
            {
                Lifecycle = lifecycle,
                HeadSha = head,
                ObservedAt = now,
                LastChangedAt = pr.Lifecycle != lifecycle || pr.HeadSha != head ? now : pr.LastChangedAt,
            };
        }
        var changes = current
            .Values.Where(pr => !previous.TryGetValue(pr.PrId, out var old) || Changed(old, pr))
            .OrderBy(pr => pr.PrId, StringComparer.Ordinal)
            .ToArray();
        // Read bindings before saving too; an unreadable local binding must not yield partial success.
        var threads = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pr in current.Values)
        {
            threads[pr.PrId] = await PrThreadBindings.GetAsync(database, repo, pr.PrId, ct);
        }
        await store.SetAsync(key, current, ct);
        foreach (var pr in current.Values.OrderBy(pr => pr.PrId, StringComparer.Ordinal))
        {
            previous.TryGetValue(pr.PrId, out var old);
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        repository = repo.DisplayName,
                        change = old is null ? "new"
                        : Changed(old, pr) ? "changed"
                        : "unchanged",
                        previous = old,
                        current = pr,
                        threadId = threads[pr.PrId],
                    }
                )
            );
        }
        Console.WriteLine(
            $"Checked {repo.DisplayName}: {active.Count} active PR(s), {changes.Length} change(s); snapshot saved."
        );
    }

    private static bool Changed(PrObservation old, PrObservation current) =>
        old.Lifecycle != current.Lifecycle
        || old.HeadSha != current.HeadSha
        || old.Title != current.Title
        || old.IsDraft != current.IsDraft
        || old.CreatedAt != current.CreatedAt
        || old.SourceUpdatedAt != current.SourceUpdatedAt
        || old.LastCommentPostedAt != current.LastCommentPostedAt;

    internal static IReadOnlyList<RepoIdentity> ParseRepos(CliOptions options)
    {
        if (options.Has("repo") == options.Has("repos"))
        {
            throw new ArgumentException("Specify exactly one of --repo or --repos.");
        }
        return
        [
            .. (options.Has("repo") ? [options.Required("repo")] : options.Required("repos").Split(','))
                .Select(ParseRepo)
                .DistinctBy(repo => repo.NormalizedKey),
        ];
    }

    /// <summary><c>org/project/repo</c> names an Azure DevOps repository; <c>owner/repo</c> a GitHub one.</summary>
    internal static RepoIdentity ParseRepo(string value)
    {
        var segments = value.Split('/', StringSplitOptions.TrimEntries);
        if (
            segments.Length is not (2 or 3)
            || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment)
                || segment.IndexOfAny(['?', '#', '%', '\\']) >= 0
                || segment.Any(char.IsControl)
                || segment is "." or ".."
            )
        )
        {
            throw new ArgumentException(
                "--repo must be org/project/repo (Azure DevOps) or owner/repo (GitHub), without URL delimiters or empty segments."
            );
        }
        return segments.Length == 2
            ? new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = segments[0],
                RepoName = segments[1],
            }
            : new RepoIdentity
            {
                Provider = "azure-devops",
                OrgOrOwner = segments[0],
                Project = segments[1],
                RepoName = segments[2],
            };
    }

    private static PrLifecycle MapLifecycle(PrLifecycleState state) =>
        state switch
        {
            PrLifecycleState.Open => PrLifecycle.Open,
            PrLifecycleState.Merged => PrLifecycle.Merged,
            PrLifecycleState.Abandoned => PrLifecycle.Abandoned,
            _ => throw new InvalidOperationException(
                "The provider returned an unrecognized PR lifecycle; no snapshot was saved."
            ),
        };

    internal sealed record PrObservation(
        string PrId,
        [property: System.Text.Json.Serialization.JsonConverter(
            typeof(System.Text.Json.Serialization.JsonStringEnumConverter)
        )]
            PrLifecycle Lifecycle,
        string? HeadSha,
        string? Title
    )
    {
        public bool? IsDraft { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? ObservedAt { get; init; }
        public DateTimeOffset? LastChangedAt { get; init; }
        public DateTimeOffset? SourceUpdatedAt { get; init; }
        public DateTimeOffset? LastCommentPostedAt { get; init; }
    }
}
