using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Misc.Middleware;

/// <summary>
///     Factory for creating tool formatters
/// </summary>
public interface IToolFormatterFactory
{
    /// <summary>
    ///     Gets a formatter for the specified tool
    /// </summary>
    ToolFormatter GetFormatter(string toolName);
}

/// <summary>
///     Default implementation of tool formatter factory that formats tool parameters as indented JSON
/// </summary>
public class DefaultToolFormatterFactory : IToolFormatterFactory
{
    private readonly Dictionary<string, JsonToolFormatter> _formatters = [];
    private readonly ConsoleColorPair _functionNameColor;

    /// <summary>
    ///     Creates a new instance of DefaultToolFormatterFactory
    /// </summary>
    /// <param name="functionNameColor">Color for function names</param>
    public DefaultToolFormatterFactory(ConsoleColorPair functionNameColor)
    {
        _functionNameColor = functionNameColor;
    }

    /// <summary>
    ///     Gets a formatter for the specified tool
    /// </summary>
    /// <param name="toolCallName">The name of the tool to get a formatter for</param>
    /// <returns>A formatter for the tool</returns>
    public ToolFormatter GetFormatter(string toolCallName)
    {
        // Get or create JsonToolFormatter for this tool
        if (!_formatters.TryGetValue(toolCallName, out var jsonFormatter))
        {
            jsonFormatter = new JsonToolFormatter();
            _formatters[toolCallName] = jsonFormatter;
        }

        return (name, fragmentUpdates) =>
        {
            var results = new List<(ConsoleColorPair, string)>();

            // Check if we have any fragment updates
            var updates = fragmentUpdates.ToList();
            if (updates.Count == 0)
            {
                // For empty updates, just show the function name
                results.Add((_functionNameColor, name + " "));
                return results;
            }

            // Use JsonToolFormatter for parameter formatting
            return jsonFormatter.Format(name, updates);
        };
    }
}
