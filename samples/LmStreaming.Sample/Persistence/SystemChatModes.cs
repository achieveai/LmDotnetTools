using LmStreaming.Sample.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Provides built-in system-defined chat modes loaded from Prompts.yaml.
/// </summary>
public static class SystemChatModes
{
    private const string PromptsFileName = "Prompts.yaml";

    /// <summary>
    /// The default mode ID.
    /// </summary>
    public const string DefaultModeId = "default";

    /// <summary>
    /// The medical knowledge mode ID.
    /// </summary>
    public const string MedicalKnowledgeModeId = "medical-knowledge";

    /// <summary>
    /// The workspace agent mode ID.
    /// </summary>
    public const string WorkspaceAgentModeId = "workspace-agent";

    /// <summary>
    /// The workflow author mode ID.
    /// </summary>
    public const string WorkflowAuthorModeId = "workflow-author";

    /// <summary>
    /// The code-review daemon's review conversation mode ID (#628). The daemon's
    /// <c>CodeReviewDaemon:LmStreamingModeId</c> defaults to this literal, so the mode is REQUIRED:
    /// <see cref="EnsureLoadedAtStartup"/> runs at host startup, and a Prompts.yaml missing this
    /// mode kills the boot with a message naming the mode and the file — instead of booting a
    /// "healthy" host that fails every mode-touching request.
    /// </summary>
    public const string CodeReviewDaemonModeId = "code-review-daemon";

    /// <summary>
    /// Lazily loads the modes on first touch. Deliberately a <see cref="Lazy{T}"/> FIELD rather
    /// than an initialized property: a property initializer would run inside the type initializer,
    /// so a bad Prompts.yaml would surface as a <see cref="TypeInitializationException"/> burying
    /// the real validation message. The field initializer only allocates the Lazy (cannot throw);
    /// the load runs on first <see cref="All"/> access and its <see cref="InvalidOperationException"/>
    /// escapes un-wrapped.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<ChatMode>> LazyAll = new(LoadModes);

    /// <summary>
    /// Gets all system-defined chat modes.
    /// </summary>
    public static IReadOnlyList<ChatMode> All => LazyAll.Value;

    /// <summary>
    /// True once <see cref="EnsureLoadedAtStartup"/> has completed. Only that method — called from
    /// Program.cs host startup — sets this, so a test that boots the host and reads the flag pins
    /// the eager call site itself, not merely the validation logic it triggers.
    /// </summary>
    internal static bool StartupLoadCompleted { get; private set; }

    /// <summary>
    /// Eagerly loads and validates the system modes. Program.cs calls this at startup, BEFORE the
    /// host starts listening, so a deployed Prompts.yaml that is broken or missing a required mode
    /// (e.g. an operator edit or a partial deploy pairing new binaries with a stale yaml) fails the
    /// boot with the clear required-mode message — rather than booting a host that looks healthy to
    /// the watchdog and 500s every mode-touching request, a failure shape the daemon retries
    /// unbounded (host 5xx is deliberately outside its retry budget).
    /// </summary>
    public static void EnsureLoadedAtStartup()
    {
        _ = LazyAll.Value;
        StartupLoadCompleted = true;
    }

    /// <summary>
    /// Gets a system mode by ID.
    /// </summary>
    /// <param name="modeId">The mode ID.</param>
    /// <returns>The system mode, or null if not found.</returns>
    public static ChatMode? GetById(string modeId)
    {
        return All.FirstOrDefault(m => m.Id == modeId);
    }

    /// <summary>
    /// Checks if a mode ID is a system-defined mode.
    /// </summary>
    /// <param name="modeId">The mode ID to check.</param>
    /// <returns>True if the mode is system-defined.</returns>
    public static bool IsSystemMode(string modeId)
    {
        return All.Any(m => m.Id == modeId);
    }

    private static IReadOnlyList<ChatMode> LoadModes()
    {
        return LoadModesFromFile(ResolvePromptsPath());
    }

    /// <summary>
    /// Loads and validates the system modes from one concrete yaml file. Split from
    /// <see cref="LoadModes"/> (which resolves the shipped Prompts.yaml) so tests can pin the
    /// required-mode boot failure — including its message naming the mode id and the file path —
    /// against a doctored file, without touching the process-wide <see cref="All"/> cache.
    /// </summary>
    internal static IReadOnlyList<ChatMode> LoadModesFromFile(string filePath)
    {
        var modes = ParseModes(File.ReadAllText(filePath));

        ValidateRequiredMode(modes, DefaultModeId, filePath);
        ValidateRequiredMode(modes, MedicalKnowledgeModeId, filePath);
        ValidateRequiredMode(modes, WorkspaceAgentModeId, filePath);
        ValidateRequiredMode(modes, CodeReviewDaemonModeId, filePath);

        return modes;
    }

    /// <summary>
    /// Binds a Prompts.yaml document to <see cref="ChatMode"/>s, enforcing the per-mode
    /// invariants (required fields, valid <c>subAgentPromptPlacement</c>, unique ids). Split from
    /// <see cref="LoadModes"/> — which adds the file-level required-mode checks — so tests can
    /// pin the binding against frozen literal yaml instead of round-tripping the shipped file.
    /// </summary>
    internal static IReadOnlyList<ChatMode> ParseModes(string yaml)
    {
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

        var document =
            deserializer.Deserialize<SystemChatModeDocument>(yaml)
            ?? throw new InvalidOperationException($"{PromptsFileName} did not contain any chat mode definitions.");

        if (document.ChatModes is null || document.ChatModes.Count == 0)
        {
            throw new InvalidOperationException($"{PromptsFileName} must define at least one chat mode.");
        }

        var now = 0L;
        var modes = document
            .ChatModes.Select(m => new ChatMode
            {
                Id = Require(m.Id, "id"),
                Name = Require(m.Name, "name"),
                Description = m.Description,
                SystemPrompt = Require(m.SystemPrompt, "systemPrompt"),
                EnabledTools = m.EnabledTools,
                EnabledBuiltInTools = m.EnabledBuiltInTools,
                EnabledCapabilityTools = m.EnabledCapabilityTools,
                SubAgentPrompt = m.SubAgentPrompt,
                SubAgentPromptPlacement = RequireValidPlacement(m.SubAgentPromptPlacement),
                SubAgentRequiredTools = m.SubAgentRequiredTools,
                IsSystemDefined = true,
                CreatedAt = now,
                UpdatedAt = now,
            })
            .ToList();

        var duplicateIds = modes
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"{PromptsFileName} contains duplicate chat mode ids: {string.Join(", ", duplicateIds)}."
            );
        }

        return modes;
    }

    private static string ResolvePromptsPath()
    {
        foreach (var start in EnumerateSearchRoots())
        {
            var direct = Path.Combine(start, PromptsFileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            var sourcePath = Path.Combine(start, "samples", "LmStreaming.Sample", PromptsFileName);
            if (File.Exists(sourcePath))
            {
                return sourcePath;
            }
        }

        throw new FileNotFoundException(
            $"Could not find {PromptsFileName}. Expected it beside the LmStreaming.Sample binaries "
                + "or at samples/LmStreaming.Sample/Prompts.yaml under the repository root."
        );
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(root);
            for (var i = 0; current is not null && i < 10; i++, current = current.Parent)
            {
                if (seen.Add(current.FullName))
                {
                    yield return current.FullName;
                }
            }
        }
    }

    private static string Require(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{PromptsFileName} contains a chat mode with a missing {fieldName}.")
            : value;
    }

    private static string? RequireValidPlacement(string? placement)
    {
        return Services.ModeSubAgentPrompt.IsValidPlacement(placement)
            ? placement
            : throw new InvalidOperationException(
                $"{PromptsFileName} contains a chat mode with an invalid subAgentPromptPlacement "
                    + $"'{placement}'. Valid values: {Services.ModeSubAgentPrompt.Prepend}, {Services.ModeSubAgentPrompt.Append}."
            );
    }

    private static void ValidateRequiredMode(IReadOnlyCollection<ChatMode> modes, string modeId, string filePath)
    {
        if (!modes.Any(m => string.Equals(m.Id, modeId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The {PromptsFileName} at '{filePath}' must define the required system mode '{modeId}'."
            );
        }
    }

    private sealed record SystemChatModeDocument
    {
        public List<SystemChatModeDefinition>? ChatModes { get; init; }
    }

    private sealed record SystemChatModeDefinition
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? Description { get; init; }

        public string? SystemPrompt { get; init; }

        public List<string>? EnabledTools { get; init; }

        public List<string>? EnabledBuiltInTools { get; init; }

        public List<string>? EnabledCapabilityTools { get; init; }

        public string? SubAgentPrompt { get; init; }

        public string? SubAgentPromptPlacement { get; init; }

        public List<string>? SubAgentRequiredTools { get; init; }
    }
}
