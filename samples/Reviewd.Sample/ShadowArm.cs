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
    string? ModeFileSha256 = null
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
        var modeSha = manifest.ModeFile is { } modeFile
            ? VariantManifest.Sha256Hex(await File.ReadAllBytesAsync(modeFile, ct))
            : null;
        return new ShadowArm(template, sha, manifest, options.Required("experiment"), modeSha);
    }

    /// <summary>
    ///     Checks that this run's workspace and mode are the ones <c>variant up</c> built from the manifest, so a receipt
    ///     never records the manifest's pins for another arm's review. The host holds that binding: the workspace's
    ///     directory and reviewer plugin, the mode's name and settings. Each pin the manifest names is checked; GETs
    ///     only.
    /// </summary>
    internal async Task CheckProvisionedAsync(
        LmStreamingS2SClient client,
        string workspaceId,
        string modeId,
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
                var differing = (await manifest.ModePayloadAsync(ct))
                    .Where(field => !JsonNode.DeepEquals(WithoutNulls(field.Value), WithoutNulls(stored[field.Key])))
                    .Select(field => field.Key)
                    .ToList();
                if (differing.Count > 0)
                {
                    throw Mismatch($"mode {modeId} differs from {file} in {string.Join(", ", differing)}");
                }
            }
        }
    }

    // The host may write an unset field as null or leave it out; both mean unset.
    private static JsonNode? WithoutNulls(JsonNode? node) =>
        node switch
        {
            JsonObject obj => new JsonObject(
                obj.Where(p => p.Value is not null).Select(p => KeyValuePair.Create(p.Key, WithoutNulls(p.Value)))
            ),
            JsonArray array => new JsonArray([.. array.Select(WithoutNulls)]),
            null => null,
            _ => node.DeepClone(),
        };

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
