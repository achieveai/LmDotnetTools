using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Misc.Middleware;

/// <summary>
/// Formats tool output as colorized JSON using structured fragment updates
/// </summary>
public class JsonToolFormatter
{
    private readonly Dictionary<string, int> _indentLevels = [];
    private readonly Dictionary<string, HashSet<string>> _processedStringsByTool = []; // Track processed strings per tool
    private static readonly ConsoleColorPair NumberColor = new() { Foreground = ConsoleColor.Cyan };
    private static readonly ConsoleColorPair BooleanColor = new() { Foreground = ConsoleColor.Yellow };
    private static readonly ConsoleColorPair NullColor = new() { Foreground = ConsoleColor.DarkGray };
    private static readonly ConsoleColorPair KeyColor = new() { Foreground = ConsoleColor.Green };
    private static readonly ConsoleColorPair StringColor = new() { Foreground = ConsoleColor.Magenta };
    private static readonly ConsoleColorPair OperatorColor = new() { Foreground = ConsoleColor.White };
    private static readonly ConsoleColorPair ColonColor = new() { Foreground = ConsoleColor.DarkYellow };
    private static readonly ConsoleColorPair CommaColor = new() { Foreground = ConsoleColor.DarkCyan };

    /// <summary>
    /// Formats structured JSON fragment updates as colorized text segments
    /// </summary>
    /// <param name="toolCallName">Name of the tool being called</param>
    /// <param name="fragmentUpdates">Structured JSON fragment updates to format</param>
    /// <returns>Sequence of colored text segments</returns>
    public IEnumerable<(ConsoleColorPair Color, string Text)> Format(
        string toolCallName,
        IEnumerable<JsonFragmentUpdate> fragmentUpdates
    )
    {
        // Initialize tracking for this tool if needed
        if (!_indentLevels.TryGetValue(toolCallName, out var indentLevel))
        {
            indentLevel = 0;
            _indentLevels[toolCallName] = indentLevel;
        }
        if (!_processedStringsByTool.TryGetValue(toolCallName, out HashSet<string>? processedStrings))
        {
            processedStrings = [];
            _processedStringsByTool[toolCallName] = processedStrings;
        }

        foreach (var update in fragmentUpdates)
        {
            // Handle indentation before tokens
            if (ShouldIndent(update.Kind))
            {
                // Special case for EndObject and EndArray - they need one less indent level
                var indentAmount =
                    update.Kind == JsonFragmentKind.EndObject || update.Kind == JsonFragmentKind.EndArray
                        ? Math.Max(0, (indentLevel - 1) * 2)
                        : (indentLevel * 2);

                yield return (OperatorColor, "\n" + new string(' ', indentAmount));
            }

            switch (update.Kind)
            {
                case JsonFragmentKind.StartObject:
                    yield return (OperatorColor, "{");
                    _indentLevels[toolCallName] = ++indentLevel;
                    break;

                case JsonFragmentKind.EndObject:
                    _indentLevels[toolCallName] = --indentLevel;
                    yield return (OperatorColor, "}");
                    break;

                case JsonFragmentKind.StartArray:
                    yield return (OperatorColor, "[");
                    _indentLevels[toolCallName] = ++indentLevel;
                    break;

                case JsonFragmentKind.EndArray:
                    _indentLevels[toolCallName] = --indentLevel;
                    yield return (OperatorColor, "]");
                    break;

                case JsonFragmentKind.Key:
                    yield return (KeyColor, update.TextValue ?? string.Empty);
                    yield return (ColonColor, ": ");
                    break;

                case JsonFragmentKind.CompleteString:
                    // Skip if we've already processed this string path.
                    // The partial strings will have already processed all
                    // the fragments, and complete string update is ignorable.
                    if (processedStrings.Contains(update.Path))
                    {
                        yield return (StringColor, "\"");
                        continue;
                    }

                    yield return (StringColor, update.TextValue ?? string.Empty);
                    _ = processedStrings.Add(update.Path);
                    break;

                case JsonFragmentKind.PartialString:
                    var value = update.TextValue ?? string.Empty;
                    if (!processedStrings.Contains(update.Path))
                    {
                        value = "\"" + value;
                    }
                    // Ensure we have quotes around the string
                    yield return (StringColor, value);
                    _ = processedStrings.Add(update.Path);
                    break;

                case JsonFragmentKind.CompleteNumber:
                    yield return (NumberColor, update.TextValue ?? string.Empty);
                    break;

                case JsonFragmentKind.CompleteBoolean:
                    yield return (BooleanColor, update.TextValue ?? string.Empty);
                    break;

                case JsonFragmentKind.CompleteNull:
                    yield return (NullColor, "null");
                    break;

                case JsonFragmentKind.JsonComplete:
                    // Reset state when JSON is complete
                    _indentLevels[toolCallName] = 0;
                    processedStrings.Clear();
                    break;
                case JsonFragmentKind.StartString:
                    break;
                default:
                    break;
            }

            // Add comma after values in arrays/objects
            if (NeedsComma(update.Kind))
            {
                yield return (CommaColor, ",");
            }
        }
    }

    private static bool ShouldIndent(JsonFragmentKind kind)
    {
        return kind switch
        {
            JsonFragmentKind.StartObject => false,
            JsonFragmentKind.EndObject => true,
            JsonFragmentKind.StartArray => false,
            JsonFragmentKind.EndArray => true,
            JsonFragmentKind.Key => true,
            _ => false,
        };
    }

    private static bool NeedsComma(JsonFragmentKind kind)
    {
        return kind switch
        {
            JsonFragmentKind.CompleteString => true,
            JsonFragmentKind.CompleteNumber => true,
            JsonFragmentKind.CompleteBoolean => true,
            JsonFragmentKind.CompleteNull => true,
            JsonFragmentKind.EndObject => true,
            JsonFragmentKind.EndArray => true,
            _ => false,
        };
    }
}
