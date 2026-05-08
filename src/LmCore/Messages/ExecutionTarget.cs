using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     Identifies where a tool call is expected to execute.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionTarget
{
    /// <summary>
    ///     Tool executes via locally-registered function handlers.
    /// </summary>
    LocalFunction,

    /// <summary>
    ///     Tool executes on the model/provider side (for example Anthropic server tools).
    /// </summary>
    ProviderServer,
}
