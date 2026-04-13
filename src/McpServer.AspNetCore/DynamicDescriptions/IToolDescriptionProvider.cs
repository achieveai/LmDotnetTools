namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore.DynamicDescriptions;

/// <summary>
/// Provides dynamic tool and parameter descriptions based on runtime context.
/// Implementations are queried during ListTools requests to customize MCP tool descriptions.
/// </summary>
/// <remarks>
/// Use this interface to create exam-specific or context-specific tool descriptions.
/// When a provider returns null, the system falls back to the default [Description] attribute.
/// </remarks>
public interface IToolDescriptionProvider
{
    /// <summary>
    /// Priority for ordering providers (lower numbers = higher priority).
    /// First matching provider wins when multiple providers support the same tool.
    /// </summary>
    int Priority => 0;

    /// <summary>
    /// Determines if this provider handles the specified tool.
    /// </summary>
    /// <param name="toolName">The MCP tool name (e.g., "UpdateQuestion")</param>
    /// <returns>True if this provider can supply descriptions for the tool</returns>
    bool SupportsToolName(string toolName);

    /// <summary>
    /// Gets the tool-level description for the specified context.
    /// </summary>
    /// <param name="toolName">The MCP tool name</param>
    /// <param name="contextKey">Context key extracted from headers (e.g., "NeetPG", "MDS")</param>
    /// <returns>Custom description, or null to use the default [Description] attribute</returns>
    string? GetToolDescription(string toolName, string? contextKey);

    /// <summary>
    /// Gets a parameter description for the specified context.
    /// </summary>
    /// <param name="toolName">The MCP tool name</param>
    /// <param name="parameterName">The parameter name (e.g., "shortExplanation")</param>
    /// <param name="contextKey">Context key extracted from headers</param>
    /// <returns>Custom description, or null to use the default [Description] attribute</returns>
    string? GetParameterDescription(string toolName, string parameterName, string? contextKey);
}
