using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Provides built-in system-defined chat modes.
/// </summary>
public static class SystemChatModes
{
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
    /// Gets all system-defined chat modes.
    /// </summary>
    public static IReadOnlyList<ChatMode> All { get; } =
    [
        new ChatMode
        {
            Id = DefaultModeId,
            Name = "General Assistant",
            Description = "A helpful assistant with access to all available tools.",
            SystemPrompt =
                "You are a helpful assistant with access to weather, calculator, and web search tools. Be concise and helpful in your responses.",
            EnabledTools = null, // All tools enabled
            IsSystemDefined = true,
            CreatedAt = 0,
            UpdatedAt = 0,
        },
        new ChatMode
        {
            Id = "math-helper",
            Name = "Math Helper",
            Description = "A focused assistant for mathematical calculations.",
            SystemPrompt =
                "You are a math assistant. Help users with calculations and mathematical problems. Use the calculator tool when needed. Be precise and show your work.",
            EnabledTools = ["calculate"],
            IsSystemDefined = true,
            CreatedAt = 0,
            UpdatedAt = 0,
        },
        new ChatMode
        {
            Id = "weather-assistant",
            Name = "Weather Assistant",
            Description = "An assistant specialized in weather information.",
            SystemPrompt =
                "You are a weather assistant. Help users get weather information for any location. Use the weather tool to provide accurate forecasts. Be friendly and informative.",
            EnabledTools = ["get_weather"],
            IsSystemDefined = true,
            CreatedAt = 0,
            UpdatedAt = 0,
        },
        new ChatMode
        {
            Id = "research-assistant",
            Name = "Research Assistant",
            Description =
                "An assistant that can search the web for up-to-date information using server-side web search.",
            SystemPrompt =
                "You are a research assistant with access to web search. When the user asks questions that require up-to-date information, current events, or facts you're unsure about, use your web search capability to find accurate answers. Cite your sources when providing information from web searches.",
            EnabledTools = ["calculate", "get_weather", "web_search"],
            IsSystemDefined = true,
            CreatedAt = 0,
            UpdatedAt = 0,
        },
        new ChatMode
        {
            Id = MedicalKnowledgeModeId,
            Name = "Medical Knowledge Assistant",
            Description =
                "A medical knowledge assistant that can search textbooks and reference materials to answer clinical questions.",
            SystemPrompt =
                "You are a medical knowledge assistant with access to textbook search tools. "
                + "When answering questions, use the book search tools to find relevant passages from medical textbooks. "
                + "Cite the source (book, chapter, page) for each fact you reference. "
                + "Be precise, evidence-based, and acknowledge uncertainty when the literature is unclear.",
            EnabledTools = [], // No local/built-in tools; only MCP book search tools
            IsSystemDefined = true,
            CreatedAt = 0,
            UpdatedAt = 0,
        },
        new ChatMode
        {
            Id = WorkspaceAgentModeId,
            Name = "Workspace Agent",
            Description =
                "Operates on a sandboxed workspace — reads/edits files and runs commands through the isolated sandbox.",
            SystemPrompt =
                "You are a workspace agent operating on a sandboxed working directory (its absolute path is given to you below). "
                + "You MUST use the sandbox tools (Read, Write, Edit, Glob, Grep, Bash, PowerShell) for ALL file and command operations in that workspace; never assume file contents or guess command output. "
                + "Path rules: the shell tools (Bash, PowerShell) already start IN the workspace directory, so relative paths work there. "
                + "The file tools (Read, Write, Edit, Glob, Grep) require ABSOLUTE paths under the workspace directory — pass the workspace's absolute path as the base (and use the 'path' argument to scope Glob/Grep). "
                + "Always Read a file before you Write or Edit it; to create a NEW file, Read it first (it will report 'not found') and then Write — this satisfies the read-before-write safeguard. "
                + "The workspace is shared and persists across the entire conversation, so be careful with destructive commands (deleting, overwriting, or force operations) and confirm intent before running them. "
                + "When a relevant Skill is listed in the available tools (for example a repo-explorer skill), prefer invoking the Skill tool to follow its documented procedure rather than improvising your own steps. "
                + "When using PowerShell, pass a distinct context_id per independent task so that parallel work does not share the same current directory or environment. "
                + "Work step-by-step: explore the workspace before editing, always read a file before writing or editing it, and summarize what you changed when you are done.",
            EnabledTools = [], // No local sample tools; tools come from the sandbox MCP server
            IsSystemDefined = true,
            CreatedAt = 0,
            UpdatedAt = 0,
        },
    ];

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
}
