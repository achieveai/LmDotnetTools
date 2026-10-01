namespace LmStreaming.Sample.Services;

/// <summary>
/// One named dual-layer pairing from the <c>DualLayerModels</c> configuration section. To the user it
/// is one model: picking it runs the conversation with <see cref="PlannerId"/> as the planner and
/// <see cref="ExecutorId"/> as the executor that carries out every tool call.
/// </summary>
/// <param name="Id">The provider id the pair is selected by. Lowercase, unique across the whole catalog.</param>
/// <param name="PlannerId">Provider id of the model that thinks and talks to the human.</param>
/// <param name="ExecutorId">Provider id of the model that owns the real tools.</param>
/// <param name="DisplayName">Label for the model picker.</param>
public sealed record DualLayerModelPreset(string Id, string PlannerId, string ExecutorId, string DisplayName);

/// <summary>
/// Reads <c>DualLayerModels</c>: <c>{ "astuna": { "Planner": "gpt-6-astra", "Executor": "gpt-6-luna",
/// "DisplayName": "Astuna" } }</c>. A key starting with <c>_</c> is a comment. An entry missing either
/// model is logged and dropped; whether the models exist is the registry's decision, not this loader's.
/// </summary>
internal static class DualLayerModelPresets
{
    public const string SectionName = "DualLayerModels";

    public static IReadOnlyList<DualLayerModelPreset> Load(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var presets = new List<DualLayerModelPreset>();
        foreach (var entry in configuration.GetSection(SectionName).GetChildren())
        {
            if (entry.Key.StartsWith('_'))
            {
                continue;
            }

            var id = entry.Key.Trim().ToLowerInvariant();
            var planner = entry["Planner"]?.Trim().ToLowerInvariant();
            var executor = entry["Executor"]?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(planner) || string.IsNullOrEmpty(executor))
            {
                logger.LogError(
                    "DualLayerModels entry '{PresetId}' needs both Planner and Executor; it is dropped",
                    entry.Key
                );
                continue;
            }

            if (string.Equals(planner, executor, StringComparison.Ordinal))
            {
                // Legal, but it defeats the point: the planner would pay full price for a summary of itself.
                logger.LogWarning("DualLayerModels entry '{PresetId}' pairs {Model} with itself", id, planner);
            }

            var displayName = entry["DisplayName"]?.Trim();
            presets.Add(
                new DualLayerModelPreset(
                    id,
                    planner,
                    executor,
                    string.IsNullOrEmpty(displayName) ? $"{id} ({planner} + {executor})" : displayName
                )
            );
        }

        return presets;
    }
}

/// <summary>
/// Host-wide tuning for every dual-layer pair, from the <c>DualLayer</c> section. Each prompt file, when
/// set, replaces the matching built-in text of <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer.DualLayerPrompts"/>;
/// <see cref="ShareReferenceContext"/> false leaves the executor with only the planner's calls. The eval
/// runs one host per variant, so a variant is these settings.
/// </summary>
/// <param name="PairInstructions">Replaces the pair explanation both layers get, or null for the built-in.</param>
/// <param name="PlannerInstructions">Replaces the planner's contract, or null for the built-in.</param>
/// <param name="ExecutorInstructions">Replaces the executor's instructions, or null for the built-in.</param>
/// <param name="ShareReferenceContext">
/// Whether the executor gets a copy of the planner's inputs. Off unless configured: in the dual-layer
/// eval (evals/dual-layer-eval/FINDINGS.md, finding 11) an executor that could see the user's task
/// answered it instead of the planner's call and wrote the answers itself, which the planner then
/// submitted. With the copy off it can only act on the call it was given.
/// </param>
/// <param name="MapRead">
/// Whether the planner gets the <c>map_read</c> tool: one call reads many documents with parallel
/// cheap readers and returns one line per document (eval direction D5). On unless configured off:
/// on the must-read-everything task it held Opus's score at 0.60x the cost (m4, 3 seeds), and the
/// built-in planner prompt tells the planner how to use it.
/// </param>
/// <param name="MapReadParallelism">Readers running at once for one <c>map_read</c> call.</param>
public sealed record DualLayerTuning(
    string? PairInstructions,
    string? PlannerInstructions,
    string? ExecutorInstructions,
    bool ShareReferenceContext,
    bool MapRead = true,
    int MapReadParallelism = 8
)
{
    public const string SectionName = "DualLayer";

    /// <summary>
    /// Reads the section. A named file that cannot be read stops startup: a run on the built-in text
    /// while its variant says otherwise would be measured as the variant.
    /// </summary>
    public static DualLayerTuning Load(IConfiguration configuration, string contentRoot, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var section = configuration.GetSection(SectionName);
        var tuning = new DualLayerTuning(
            ReadFile(section, "PairInstructionsFile", contentRoot, logger),
            ReadFile(section, "PlannerInstructionsFile", contentRoot, logger),
            ReadFile(section, "ExecutorInstructionsFile", contentRoot, logger),
            section.GetValue("ShareReferenceContext", false),
            section.GetValue("MapRead", true),
            section.GetValue("MapReadParallelism", 8)
        );
        if (tuning.MapRead)
        {
            logger.LogInformation(
                "Dual layer: the planner gets map_read ({Parallelism} parallel readers)",
                tuning.MapReadParallelism
            );
        }
        if (tuning.ShareReferenceContext)
        {
            logger.LogInformation("Dual layer: the executor gets a reference copy of the planner's inputs");
        }

        return tuning;
    }

    private static string? ReadFile(IConfigurationSection section, string key, string contentRoot, ILogger logger)
    {
        var path = section[key];
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path, contentRoot);
        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{SectionName}:{key} names '{fullPath}', which cannot be read.", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"{SectionName}:{key} names '{fullPath}', which is empty.");
        }

        logger.LogInformation(
            "Dual layer: {Key} from {Path} ({Sha256})",
            key,
            fullPath,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[
                ..12
            ]
        );
        return text;
    }
}
