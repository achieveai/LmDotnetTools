using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;

namespace Reviewd.Sample;

/// <summary>
///     <c>variant up</c>: builds one A/B arm's workspace from its manifest (docs/plans/2026-11-16-reviewer-evolution-ab.md,
///     "How an arm is built"). The champion and every challenger are built by this same command, from the same base
///     workspace, so the only differences between arms are the ones their manifests name. It verifies the arm's
///     review-repo clone but never moves it: checking out a branch under busy review slots is the operator's call.
/// </summary>
internal static class VariantUp
{
    /// <summary>The plugin an arm takes from its own marketplace alias; every other plugin stays the base's.</summary>
    internal const string ReviewerPlugin = "code-reviewer";

    /// <summary>Registers (or re-points) an app marketplace alias at a commit and returns the commit it resolved.</summary>
    internal delegate Task<string> RegisterMarketplace(string alias, string url, string sha, CancellationToken ct);

    /// <summary>
    ///     A review-repo clone: the commit it has checked out, and the <c>KnowledgeBase/</c> paths whose content differs
    ///     between that commit and the champion's.
    /// </summary>
    internal sealed record Clone(string Head, IReadOnlyList<string> KnowledgeChanges);

    /// <summary>Reads a workspace directory's review-repo clone against champion commit <c>champion</c>.</summary>
    internal delegate Task<Clone> ReadClone(string directory, string champion, CancellationToken ct);

    /// <summary>Runs git with <c>arguments</c> in the clone and returns its trimmed output; fails naming <c>what</c>.</summary>
    internal delegate Task<string> Git(string what, string[] arguments, CancellationToken ct);

    /// <summary>
    ///     The clone's head, and every <c>KnowledgeBase/</c> path whose content differs from champion commit
    ///     <paramref name="champion"/>: a tree comparison, so an arm based on an older commit than the champion is
    ///     caught as surely as one that edits the KnowledgeBase.
    /// </summary>
    internal static async Task<Clone> ReadCloneAsync(Git git, string champion, CancellationToken ct)
    {
        var head = await git("git rev-parse HEAD", ["rev-parse", "HEAD"], ct);
        var changes = await git(
            $"Comparing KnowledgeBase/ with champion {champion} (fetch it into the clone first)",
            ["diff", "--name-only", champion, head, "--", "KnowledgeBase/"],
            ct
        );
        return new Clone(head, changes.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    ///     Reads a workspace directory's clone with <see cref="ReadCloneAsync"/>, through the git <paramref name="open"/>
    ///     returns for that directory.
    /// </summary>
    internal static ReadClone CloneReader(Func<string, CancellationToken, Task<Git>> open) =>
        async (directory, champion, ct) => await ReadCloneAsync(await open(directory, ct), champion, ct);

    public static async Task<int> RunAsync(CliOptions options, ReviewdConfig config, CancellationToken ct)
    {
        using var sandbox = config.CreateSandboxClient();
        var result = await RunCoreAsync(
            options,
            config.CreateS2SClient(),
            async (alias, url, sha, token) =>
                (await sandbox.RegisterAppMarketplaceAsync(alias, url, sha, token)).ResolvedSha,
            CloneReader(
                async (directory, token) =>
                {
                    var session = await sandbox.CreateAsync(new SandboxCreateRequest(directory, []), token);
                    return async (what, arguments, cancellation) =>
                    {
                        var run = await sandbox.ExecuteAsync(
                            session.SessionId,
                            new SandboxCommand(["git", .. arguments], "."),
                            cancellation
                        );
                        return run.ExitCode == 0
                            ? run.StandardOutput.Trim()
                            : throw new InvalidOperationException(
                                $"{what} failed in {directory}: {run.StandardError.Trim()}"
                            );
                    };
                }
            ),
            ct
        );
        Console.WriteLine(result.ToJsonString());
        return 0;
    }

    internal static async Task<JsonObject> RunCoreAsync(
        CliOptions options,
        LmStreamingS2SClient client,
        RegisterMarketplace register,
        ReadClone readClone,
        CancellationToken ct
    )
    {
        var manifest = await VariantManifest.LoadAsync(options.Required("manifest"), ct);
        var experiment = options.Required("experiment");
        var champion = options.Required("champion");
        var missing = new (string Name, string? Value)[]
        {
            ("reviewRepo.directory", manifest.ReviewRepoDirectory),
            ("reviewRepo.sha", manifest.ReviewRepoSha),
            ("plugins.alias", manifest.PluginAlias),
            ("plugins.url", manifest.PluginUrl),
            ("plugins.sha", manifest.PluginSha),
            ("mode.name", manifest.ModeName),
            ("mode.file", manifest.ModeFile),
        }
            .Where(field => field.Value is null)
            .Select(field => field.Name)
            .ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"Variant manifest {manifest.Id} must pin {string.Join(", ", missing)} for variant up."
            );
        }
        var mode = await manifest.ModePayloadAsync(ct);

        // The arm is the base workspace with the reviewer plugin taken from the arm's own alias.
        var source = await client.GetWorkspaceAsync(options.Required("from-workspace"), ct);
        if (source.PluginSelection is not { } baseSelection || !baseSelection.Any(p => p.Plugin == ReviewerPlugin))
        {
            throw new InvalidOperationException(
                $"Base workspace {source.Id} does not enable {ReviewerPlugin} explicitly, so an arm cannot replace it."
            );
        }
        var alias = manifest.PluginAlias!;
        IReadOnlyList<SandboxPluginRef> selection =
        [
            .. baseSelection.Select(p => p.Plugin == ReviewerPlugin ? new SandboxPluginRef(alias, p.Plugin) : p),
        ];
        IReadOnlyList<string> marketplaces = [.. source.Marketplaces.Append(alias).Distinct(StringComparer.Ordinal)];
        var name = $"variant-{manifest.Id}";
        var existing = (await client.ListWorkspacesAsync(ct)).SingleOrDefault(w => w.Name == name);
        if (existing is not null && existing.DirectoryRelPath != manifest.ReviewRepoDirectory)
        {
            throw new InvalidOperationException(
                $"Workspace {name} ({existing.Id}) uses directory {existing.DirectoryRelPath}, not the manifest's "
                    + $"{manifest.ReviewRepoDirectory}; remove or rename it first."
            );
        }

        var result = new JsonObject
        {
            ["command"] = "variant up",
            ["variant"] = manifest.Id,
            ["experimentId"] = experiment,
            ["champion"] = champion,
            ["manifestSha256"] = manifest.Sha256,
            ["workspaceName"] = name,
            ["directory"] = manifest.ReviewRepoDirectory,
            ["marketplaces"] = new JsonArray([.. marketplaces.Select(m => (JsonNode)m)]),
            ["plugins"] = new JsonObject
            {
                ["alias"] = alias,
                ["url"] = manifest.PluginUrl,
                ["sha"] = manifest.PluginSha,
            },
            ["mode"] = manifest.ModeName,
        };
        if (!options.Has("execute"))
        {
            result["preview"] = true;
            result["workspaceId"] = existing?.Id;
            result["next"] = "Run again with --execute to build the arm.";
            return result;
        }

        // The clone is checked first, so an arm on the wrong commit, or one that changes the KnowledgeBase, changes
        // nothing.
        var clone = await readClone(manifest.ReviewRepoDirectory!, champion, ct);
        var head = clone.Head;
        if (head != manifest.ReviewRepoSha)
        {
            throw new InvalidOperationException(
                $"The review-repo clone in {manifest.ReviewRepoDirectory} is at {head}, but manifest {manifest.Id} pins "
                    + $"{manifest.ReviewRepoSha}. Check out {manifest.ReviewRepoRef ?? manifest.ReviewRepoSha} there "
                    + "while no slot is busy, then run variant up again."
            );
        }
        // Each arm's reviews are judged in its own workspace, by a judge that reads that workspace's KnowledgeBase.
        // An arm holding other knowledge than the experiment's frozen champion would be reviewed and judged with it, so
        // no arm may: not one that edits it, nor one based on an older or newer commit.
        if (clone.KnowledgeChanges.Count > 0)
        {
            throw new InvalidOperationException(
                $"Manifest {manifest.Id}'s commit holds a different KnowledgeBase/ from champion {champion} "
                    + $"({string.Join(", ", clone.KnowledgeChanges.Take(3))}{(clone.KnowledgeChanges.Count > 3 ? ", ..." : "")}). "
                    + "Every arm reviews and is judged with the champion's knowledge; base the arm on it."
            );
        }
        var resolved = await register(alias, manifest.PluginUrl!, manifest.PluginSha!, ct);
        if (resolved != manifest.PluginSha)
        {
            throw new InvalidOperationException(
                $"Marketplace alias {alias} resolved to {resolved}, not the manifest's {manifest.PluginSha}."
            );
        }
        var modeId = await client.EnsureChatModeAsync(manifest.ModeName!, mode, ct);
        var workspace = existing is null
            ? await client.CreateWorkspaceAsync(name, manifest.ReviewRepoDirectory!, marketplaces, ct, selection)
            : await client.UpdateWorkspaceAsync(
                await client.GetWorkspaceAsync(existing.Id, ct),
                marketplaces,
                replaceSelection: true,
                selection,
                ct
            );

        result["workspaceId"] = workspace.Id;
        result["modeId"] = modeId;
        result["reviewRepoSha"] = head;
        var taskPrompt = manifest.TaskPromptFile is { } file ? $" --task-prompt-file {file}" : "";
        var effort = manifest.Effort is { } level ? $" --reasoning-effort {level}" : "";
        result["next"] =
            $"sandbox create --workspace {manifest.ReviewRepoDirectory} --callback <url> --read-only-egress "
            + "--read-repos <org/project/repo,... every repo this arm reviews>, then "
            + $"review batch --selection <json> --db <this arm's db> --session <session> --workspace {workspace.Id} "
            + $"--provider {manifest.Provider ?? "<provider>"} --mode {modeId}{effort} --shadow --post-session <read-only "
            + $"session> --variant {manifest.Id} --manifest {manifest.Path} --experiment {experiment}{taskPrompt} --execute";
        return result;
    }
}
