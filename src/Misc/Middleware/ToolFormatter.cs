using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Misc.Middleware;

/// <summary>
///     Delegate for formatting tool output into colored text segments using structured JSON fragment updates
/// </summary>
/// <param name="toolCallName">Name of the tool being called</param>
/// <param name="fragmentUpdates">Structured JSON fragment updates from the tool</param>
/// <returns>Sequence of colored text segments</returns>
public delegate IEnumerable<(ConsoleColorPair Color, string Text)> ToolFormatter(
    string toolCallName,
    IEnumerable<JsonFragmentUpdate> fragmentUpdates
);
