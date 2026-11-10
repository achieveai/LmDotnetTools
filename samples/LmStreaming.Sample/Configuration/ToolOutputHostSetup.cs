using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace LmStreaming.Sample.Configuration;

/// <summary>Reads this host's settings for saving large tool output (#930 item 2).</summary>
public static class ToolOutputHostSetup
{
    public const string MaxInlineBytesKey = "ToolOutput:MaxInlineBytes";

    /// <summary>
    ///     The largest tool output kept inline. An absent setting gives the library default.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is below the library's minimum.</exception>
    public static int ReadMaxInlineBytes(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration.GetValue(MaxInlineBytesKey, ToolOutputSpillOptions.DefaultMaxInlineBytes);
        return value >= ToolOutputSpillOptions.MinInlineBytes
            ? value
            : throw new InvalidOperationException(
                $"{MaxInlineBytesKey} must be at least {ToolOutputSpillOptions.MinInlineBytes}, but is {value}."
            );
    }
}
