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
    /// <c>CodeReviewDaemon:LmStreamingModeId</c> defaults to this literal, so the mode is REQUIRED
    /// at load: a review host that boots without it would 404 every provision instead of failing
    /// here, at the moment the operator can still read the message.
    /// </summary>
    public const string CodeReviewDaemonModeId = "code-review-daemon";

    /// <summary>
    /// Gets all system-defined chat modes.
    /// </summary>
    public static IReadOnlyList<ChatMode> All { get; } = LoadModes();

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
        var filePath = ResolvePromptsPath();
        var modes = ParseModes(File.ReadAllText(filePath));

        ValidateRequiredMode(modes, DefaultModeId);
        ValidateRequiredMode(modes, MedicalKnowledgeModeId);
        ValidateRequiredMode(modes, WorkspaceAgentModeId);
        ValidateRequiredMode(modes, CodeReviewDaemonModeId);

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

    private static void ValidateRequiredMode(IReadOnlyCollection<ChatMode> modes, string modeId)
    {
        if (!modes.Any(m => string.Equals(m.Id, modeId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"{PromptsFileName} must define the required system mode '{modeId}'.");
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
