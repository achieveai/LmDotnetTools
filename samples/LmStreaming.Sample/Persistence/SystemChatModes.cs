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
    /// Gets all system-defined chat modes.
    /// </summary>
    public static IReadOnlyList<ChatMode> All { get; } =
    [
        new ChatMode
        {
            Id = DefaultModeId,
            Name = "General Assistant",
            Description = "A helpful assistant with access to all available tools.",
            SystemPrompt = "You are a helpful assistant with access to weather, calculator, and web search tools. Be concise and helpful in your responses.",
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
            SystemPrompt = "You are a math assistant. Help users with calculations and mathematical problems. Use the calculator tool when needed. Be precise and show your work.",
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
            SystemPrompt = "You are a weather assistant. Help users get weather information for any location. Use the weather tool to provide accurate forecasts. Be friendly and informative.",
            EnabledTools = ["get_weather"],
            IsSystemDefined = true,
            CreatedAt = 0,
            UpdatedAt = 0,
        },
        new ChatMode
        {
            Id = "research-assistant",
            Name = "Research Assistant",
            Description = "An assistant that can search the web for up-to-date information using server-side web search.",
            SystemPrompt = "You are a research assistant with access to web search. When the user asks questions that require up-to-date information, current events, or facts you're unsure about, use your web search capability to find accurate answers. Cite your sources when providing information from web searches.",
            EnabledTools = ["calculate", "get_weather", "web_search"],
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
