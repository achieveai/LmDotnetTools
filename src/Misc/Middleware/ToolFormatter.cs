using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Misc.Middleware;

/// <summary>
/// Delegate for formatting tool output into colored text segments
/// </summary>
/// <param name="toolCallName">Name of the tool being called</param>
/// <param name="toolParameterUpdate">Current parameter update from the tool</param>
/// <returns>Sequence of colored text segments</returns>
public delegate IEnumerable<(ConsoleColorPair Color, string Text)> ToolFormatter(
    string toolCallName,
    string toolParameterUpdate
); 