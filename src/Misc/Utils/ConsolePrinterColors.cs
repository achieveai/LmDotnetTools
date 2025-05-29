namespace AchieveAi.LmDotnetTools.Misc.Utils;

/// <summary>
/// Represents a color with optional foreground and background components
/// </summary>
public record ConsoleColorPair
{
    /// <summary>
    /// Foreground color to use for console output
    /// </summary>
    public ConsoleColor? Foreground { get; init; }

    /// <summary>
    /// Background color to use for console output
    /// </summary>
    public ConsoleColor? Background { get; init; }
}

/// <summary>
/// Colors for the different message types
/// </summary>
public record ConsolePrinterColors
{
    /// <summary>
    /// Color for regular text messages
    /// </summary>
    public ConsoleColorPair TextMessageColor { get; init; } = new() { Foreground = ConsoleColor.White };

    /// <summary>
    /// Color for usage messages
    /// </summary>
    public ConsoleColorPair UsageMessageColor { get; init; } = new() { Foreground = ConsoleColor.DarkGray };

    /// <summary>
    /// Color for tool use messages
    /// </summary>
    public ConsoleColorPair ToolUseMessageColor { get; init; } = new() { Foreground = ConsoleColor.Cyan };

    /// <summary>
    /// Color for tool result messages
    /// </summary>
    public ConsoleColorPair ToolResultMessageColor { get; init; } = new() { Foreground = ConsoleColor.Green };

    /// <summary>
    /// Color for single horizontal separator lines
    /// </summary>
    public ConsoleColorPair HorizontalLineColor { get; init; } = new() { Foreground = ConsoleColor.DarkGray };

    /// <summary>
    /// Color for double horizontal separator lines at the end of a response
    /// </summary>
    public ConsoleColorPair CompletionLineColor { get; init; } = new() { Foreground = ConsoleColor.Gray };
}