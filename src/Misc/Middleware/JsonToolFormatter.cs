using AchieveAi.LmDotnetTools.LmCore.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace AchieveAi.LmDotnetTools.Misc.Middleware;

/// <summary>
/// Formats tool output as colorized JSON
/// </summary>
public class JsonToolFormatter
{
    private readonly Dictionary<string, JsonFragmentToStructuredUpdateGenerator> _accumulators = new();
    private readonly Dictionary<string, int> _indentLevels = new();
    private readonly HashSet<string> _processedStrings = new(); // Track processed strings to avoid duplicates
    private static readonly ConsoleColorPair NumberColor = new() { Foreground = ConsoleColor.Cyan };
    private static readonly ConsoleColorPair BooleanColor = new() { Foreground = ConsoleColor.Yellow };
    private static readonly ConsoleColorPair NullColor = new() { Foreground = ConsoleColor.DarkGray };
    private static readonly ConsoleColorPair KeyColor = new() { Foreground = ConsoleColor.Green };
    private static readonly ConsoleColorPair StringColor = new() { Foreground = ConsoleColor.Magenta };
    private static readonly ConsoleColorPair OperatorColor = new() { Foreground = ConsoleColor.White };
    private static readonly ConsoleColorPair ColonColor = new() { Foreground = ConsoleColor.DarkYellow };
    private static readonly ConsoleColorPair CommaColor = new() { Foreground = ConsoleColor.DarkCyan };

    /// <summary>
    /// Formats a tool update as colorized JSON fragments
    /// </summary>
    public IEnumerable<(ConsoleColorPair Color, string Text)> Format(string toolCallName, string toolParameterUpdate)
    {
        // Get or create accumulator for this tool
        if (!_accumulators.TryGetValue(toolCallName, out var accumulator))
        {
            accumulator = new JsonFragmentToStructuredUpdateGenerator(toolCallName);
            _accumulators[toolCallName] = accumulator;
            _indentLevels[toolCallName] = 0;
            _processedStrings.Clear(); // Clear the processed strings when starting fresh
        }

        // Process the update through the accumulator
        var updates = accumulator.AddFragment(toolParameterUpdate);

        foreach (var update in updates)
        {
            var indentLevel = _indentLevels[toolCallName];

            // Handle indentation before tokens
            if (ShouldIndent(update.Kind))
            {
                // Special case for EndObject and EndArray - they need one less indent level
                int indentAmount = update.Kind == JsonFragmentKind.EndObject || 
                                  update.Kind == JsonFragmentKind.EndArray ? 
                                  Math.Max(0, (indentLevel - 1) * 2) : (indentLevel * 2);
                
                yield return (OperatorColor, "\n" + new string(' ', indentAmount));
            }

            switch (update.Kind)
            {
                case JsonFragmentKind.StartObject:
                    yield return (OperatorColor, "{");
                    _indentLevels[toolCallName]++;
                    break;

                case JsonFragmentKind.EndObject:
                    _indentLevels[toolCallName]--;
                    yield return (OperatorColor, "}");
                    break;

                case JsonFragmentKind.StartArray:
                    yield return (OperatorColor, "[");
                    _indentLevels[toolCallName]++;
                    break;

                case JsonFragmentKind.EndArray:
                    _indentLevels[toolCallName]--;
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
                    if (_processedStrings.Contains(update.Path))
                    {
                        yield return (StringColor, "\"");
                        continue;
                    }
                    
                    yield return (StringColor, update.TextValue ?? string.Empty);
                    _processedStrings.Add(update.Path);
                    break;

                case JsonFragmentKind.PartialString:
                    var value = update.TextValue ?? string.Empty;
                    if (!_processedStrings.Contains(update.Path))
                    {
                        value = "\"" + value;
                    }
                    // Ensure we have quotes around the string
                    yield return (StringColor, value);
                    _processedStrings.Add(update.Path);
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
            }

            // Add comma after values in arrays/objects
            if (NeedsComma(update.Kind))
            {
                yield return (CommaColor, ",");
            }
        }
    }

    private static bool ShouldIndent(JsonFragmentKind kind) => kind switch
    {
        JsonFragmentKind.StartObject => false,
        JsonFragmentKind.EndObject => true,
        JsonFragmentKind.StartArray => false,
        JsonFragmentKind.EndArray => true,
        JsonFragmentKind.Key => true,
        _ => false
    };

    private static bool NeedsComma(JsonFragmentKind kind) => kind switch
    {
        JsonFragmentKind.CompleteString => true,
        JsonFragmentKind.CompleteNumber => true,
        JsonFragmentKind.CompleteBoolean => true,
        JsonFragmentKind.CompleteNull => true,
        JsonFragmentKind.EndObject => true,
        JsonFragmentKind.EndArray => true,
        _ => false
    };
} 