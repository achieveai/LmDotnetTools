using System.Reflection;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Agents;

namespace Reviewd.Sample;

/// <summary>
///     What a shadow review run is, resolved once before anything is written: its task prompt template and, for a
///     variant, the manifest it must agree with. A run whose flags disagree with its manifest is refused, so an arm's
///     results never carry a label for a review the manifest did not describe.
/// </summary>
internal sealed record ShadowArm(
    string Template,
    string TemplateSha256,
    VariantManifest? Manifest,
    string? Experiment,
    string? ModeFileSha256 = null,
    JsonObject? ModePayload = null
)
{
    /// <summary>The live run has none; a shadow run gets the default template unless --task-prompt-file names one.</summary>
    internal static async Task<ShadowArm?> ResolveAsync(CliOptions options, string provider, CancellationToken ct)
    {
        if (!options.Has("shadow"))
        {
            return null;
        }
        var template = options.Get("task-prompt-file") is { } file
            ? await File.ReadAllTextAsync(file, ct)
            : ShadowTaskPrompt.Default;
        ShadowTaskPrompt.Validate(template, options.Get("task-prompt-file") ?? "the default task prompt");
        var sha = VariantManifest.Sha256Hex(template);
        if (options.Get("manifest") is not { } manifestPath)
        {
            return new ShadowArm(template, sha, null, null);
        }

        // A variant arm reviews only under an experiment record the study store holds (plan: "no batch runs without an
        // experiment record").
        StudyStore.RequireExperiment(options.Required("store"), options.Required("experiment"));
        var manifest = await VariantManifest.LoadAsync(manifestPath, ct);
        var variant = options.Required("variant");
        void Agree(string what, string? pinned, string? actual)
        {
            if (pinned is not null && pinned != actual)
            {
                throw new ArgumentException(
                    $"Variant manifest {manifest.Id} pins {what} {pinned}, but this run uses {actual ?? "none"}."
                );
            }
        }
        Agree("id", manifest.Id, variant);
        // The mode is pinned by name and file: the host mints mode ids, so variant up prints the one to pass as --mode.
        Agree("provider", manifest.Provider, provider);
        Agree("reasoning effort", manifest.Effort, options.Get("reasoning-effort"));
        // The manifest's task prompt, or the default one when it names none, is the one this run must send.
        var pinnedSha = manifest.TaskPromptFile is { } pinnedFile
            ? VariantManifest.Sha256Hex(await File.ReadAllTextAsync(pinnedFile, ct))
            : VariantManifest.Sha256Hex(ShadowTaskPrompt.Default);
        Agree("task prompt SHA-256", pinnedSha, sha);
        // A run checks a pinned commit or mode file through the workspace or mode it belongs to, so it needs that too.
        foreach (
            var (pin, anchor) in new (string?, string?)[]
            {
                (manifest.ReviewRepoSha is null ? null : "reviewRepo.sha", manifest.ReviewRepoDirectory),
                (manifest.PluginSha is null ? null : "plugins.sha", manifest.PluginAlias),
                (manifest.ModeFile is null ? null : "mode.file", manifest.ModeName),
            }
        )
        {
            if (pin is not null && anchor is null)
            {
                throw new ArgumentException(
                    $"Variant manifest {manifest.Id} pins {pin} without the directory, alias or mode name a run checks it by."
                );
            }
        }
        // The mode file is read once: the hash the receipt and a resume record names the very payload the run checks.
        if (manifest.ModeFile is not { } modeFile)
        {
            return new ShadowArm(template, sha, manifest, options.Required("experiment"));
        }
        var modeBytes = await File.ReadAllBytesAsync(modeFile, ct);
        return new ShadowArm(
            template,
            sha,
            manifest,
            options.Required("experiment"),
            VariantManifest.Sha256Hex(modeBytes),
            manifest.ModePayload(modeBytes)
        );
    }

    /// <summary>
    ///     Checks that this run's workspace and mode are the ones <c>variant up</c> built from the manifest, so a receipt
    ///     never records the manifest's pins for another arm's review. The host holds that binding: the workspace's
    ///     directory and reviewer plugin, the mode's name and settings. Each pin the manifest names is checked; GETs
    ///     only. Then the commits a review would run, which can move after the arm was built: the review-repo clone's,
    ///     through <paramref name="reviewRepoHead"/>, and the one the reviewer's marketplace alias resolves to, through
    ///     <paramref name="aliasSha"/> (a new sandbox mounts the alias's current commit). A run that executes passes
    ///     both; a preview writes nothing and passes null.
    /// </summary>
    internal async Task CheckProvisionedAsync(
        LmStreamingS2SClient client,
        string workspaceId,
        string modeId,
        Func<CancellationToken, Task<string?>>? reviewRepoHead,
        Func<string, CancellationToken, Task<string?>>? aliasSha,
        CancellationToken ct
    )
    {
        if (Manifest is not { } manifest)
        {
            return;
        }
        InvalidOperationException Mismatch(string what) =>
            new(
                $"Manifest {manifest.Id} was not provisioned as this run's arm: {what}. Run variant up for it and pass "
                    + "the workspace and mode it prints."
            );
        if (manifest.ReviewRepoDirectory is not null || manifest.PluginAlias is not null)
        {
            var workspace = await client.GetWorkspaceAsync(workspaceId, ct);
            if (manifest.ReviewRepoDirectory is { } directory && workspace.DirectoryRelPath != directory)
            {
                throw Mismatch($"workspace {workspaceId} uses directory {workspace.DirectoryRelPath}, not {directory}");
            }
            if (manifest.PluginAlias is { } alias)
            {
                var sources = (workspace.PluginSelection ?? [])
                    .Where(p => p.Plugin == VariantUp.ReviewerPlugin)
                    .Select(p => p.Marketplace)
                    .ToList();
                if (sources is not [var only] || only != alias)
                {
                    throw Mismatch(
                        $"workspace {workspaceId} takes {VariantUp.ReviewerPlugin} from "
                            + $"{(sources.Count == 0 ? "no marketplace" : string.Join(", ", sources))}, not {alias}"
                    );
                }
            }
        }
        if (manifest.ModeName is { } name)
        {
            var stored = await client.GetChatModeAsync(modeId, ct);
            if (stored["name"]?.ToString() != name)
            {
                throw Mismatch($"mode {modeId} is named '{stored["name"]}', not '{name}'");
            }
            if (manifest.ModeFile is { } file)
            {
                var differing = ModeDifferences(ModePayload!, stored);
                if (differing.Count > 0)
                {
                    throw Mismatch($"mode {modeId} differs from {file} in {string.Join(", ", differing)}");
                }
            }
        }
        // The receipt records the manifest's commit, so the clone must have it checked out now, not only when the arm
        // was built.
        if (manifest.ReviewRepoSha is { } pinned && reviewRepoHead is not null)
        {
            var head = await reviewRepoHead(ct);
            if (head != pinned)
            {
                throw Mismatch(
                    $"the review-repo clone in {manifest.ReviewRepoDirectory} is at {head ?? "an unreadable commit"}, "
                        + $"not {pinned}"
                );
            }
        }
        if (manifest.PluginSha is { } pluginSha && aliasSha is not null)
        {
            var resolved = await aliasSha(manifest.PluginAlias!, ct);
            if (resolved != pluginSha)
            {
                throw Mismatch(
                    $"marketplace alias {manifest.PluginAlias} resolves to {resolved ?? "nothing (not registered)"}, "
                        + $"not {pluginSha}"
                );
            }
        }
    }

    /// <summary>
    ///     The fields of mode body <paramref name="payload"/> whose value the host's <paramref name="stored"/> mode does
    ///     not hold, ignoring unset fields. Both variant up, after it saves the mode, and every run compare this way.
    /// </summary>
    internal static List<string> ModeDifferences(JsonObject payload, JsonNode stored) =>
        [
            .. payload
                .Where(field =>
                    !SameModeValue(
                        field.Value,
                        stored[field.Key],
                        S_modeMaps.Contains(field.Key) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase
                    )
                )
                .Select(field => field.Key),
        ];

    // The mode fields that are maps, not records: their keys are data the host keeps as written and matches exactly
    // (an env name in another letter case is another variable), so they compare exactly.
    private static readonly HashSet<string> S_modeMaps = ["env", "subAgentModelIntelligenceByType"];

    // The host binds a mode's names in any letter case and stores its own (tiers.Caps.Skill comes back as
    // caps.skill), and may write an unset field as null or leave it out; both mean unset. Values compare exactly, so a
    // number written as a string, or a field the host does not keep, still differs.
    private static bool SameModeValue(JsonNode? file, JsonNode? stored, StringComparison names) =>
        (file, stored) switch
        {
            (JsonObject a, JsonObject b) => SameModeFields(a, b, names),
            (JsonArray a, JsonArray b) => a.Count == b.Count
                && a.Zip(b).All(pair => SameModeValue(pair.First, pair.Second, names)),
            (JsonObject or JsonArray, _) or (_, JsonObject or JsonArray) => false,
            _ => JsonNode.DeepEquals(file, stored),
        };

    private static bool SameModeFields(JsonObject file, JsonObject stored, StringComparison names)
    {
        var set = stored.Where(p => p.Value is not null).ToList();
        var written = file.Where(p => p.Value is not null).ToList();
        return written.Count == set.Count
            && written.All(field =>
                set.Where(p => string.Equals(p.Key, field.Key, names)).ToList() is [var match]
                && SameModeValue(field.Value, match.Value, names)
            );
    }

    /// <summary>
    ///     Why a resume of an attempt that recorded <paramref name="sent"/> as its provenance may not run as this arm, or
    ///     null when it may: the task prompt, manifest, experiment and mode file must be the ones it started with.
    /// </summary>
    internal string? ResumeMismatch(JsonObject sent, string mode, string provider, string? effort)
    {
        // A resume keeps the attempt's reviewer and the plugin commit its sandbox mounted. Only an attempt whose start
        // checked the clone's and the alias's commits is known to have mounted the manifest's.
        if (
            (Manifest?.ReviewRepoSha ?? Manifest?.PluginSha) is not null
            && sent[PinsVerified]?.ToJsonString() != "true"
        )
        {
            return "This attempt started before a run checked its manifest's clone and alias commits, so its reviewer "
                + "may have mounted another plugin commit. Release it and review the PR again.";
        }
        foreach (var (field, value) in Provenance(mode, provider, effort))
        {
            if (
                field is "taskPromptSha256" or "manifestSha256" or "experimentId" or "modeFileSha256"
                && sent[field]?.ToString() != value?.ToString()
            )
            {
                return $"This attempt was started with {field} {sent[field]?.ToString() ?? "none"}; a resume must use "
                    + $"the same arm, not {value?.ToString() ?? "none"}.";
            }
        }
        return null;
    }

    /// <summary>
    ///     The provenance field a run sets when its start checked the manifest's clone and alias commits, before it
    ///     provisioned the reviewer.
    /// </summary>
    internal const string PinsVerified = "pinsVerified";

    /// <summary>The receipt's record of what produced the review (plan build item 4).</summary>
    internal JsonObject Provenance(string mode, string provider, string? effort) =>
        new()
        {
            ["reviewdBuild"] = ReviewdBuild,
            ["mode"] = mode,
            ["provider"] = provider,
            ["reasoningEffort"] = effort,
            ["taskPromptSha256"] = TemplateSha256,
            ["experimentId"] = Experiment,
            ["manifestSha256"] = Manifest?.Sha256,
            ["modeFileSha256"] = ModeFileSha256,
            ["reviewRepoSha"] = Manifest?.ReviewRepoSha,
            ["pluginSha"] = Manifest?.PluginSha,
        };

    /// <summary>The Reviewd version and the commit it was built from ("1.0.33+&lt;sha&gt;").</summary>
    internal static string ReviewdBuild { get; } =
        typeof(ShadowArm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
