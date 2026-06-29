using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmWorkflow;

/// <summary>
///     Parses the injected background-completion message a <c>SubAgentManager</c> relays to the parent loop:
///     a <c>&lt;sub-agent name="…" id="…"&gt;</c> block whose body is either
///     <c>[Completed] Task: …\nResult: …</c> or <c>[Error] Task: …\nError: …</c>. The correlation key is the
///     <c>id</c> attribute (the generated agent id), <b>not</b> the <c>name</c> attribute (which is the
///     template name). The parse logic lives here — small and unit-tested — rather than buried in the session.
/// </summary>
internal static partial class SubAgentResultParser
{
    /// <summary>
    ///     Attempts to parse <paramref name="text"/> as a sub-agent completion block. On success
    ///     <paramref name="id"/> is the agent id, <paramref name="payload"/> is the trimmed
    ///     <c>Result:</c>/<c>Error:</c> body, and <paramref name="isError"/> indicates an error completion.
    ///     Returns <c>false</c> for any text that is not a recognizable sub-agent block.
    /// </summary>
    public static bool TryParse(string? text, out string id, out string payload, out bool isError)
    {
        id = string.Empty;
        payload = string.Empty;
        isError = false;

        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith("<sub-agent", StringComparison.Ordinal))
        {
            return false;
        }

        var idMatch = IdAttribute().Match(text);
        if (!idMatch.Success)
        {
            return false;
        }

        id = idMatch.Groups[1].Value;

        var result = ResultPayload().Match(text);
        if (result.Success)
        {
            isError = false;
            payload = result.Groups[1].Value.Trim();
            return true;
        }

        var error = ErrorPayload().Match(text);
        if (error.Success)
        {
            isError = true;
            payload = error.Groups[1].Value.Trim();
            return true;
        }

        return false;
    }

    [GeneratedRegex("\\bid\\s*=\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex IdAttribute();

    [GeneratedRegex(
        "\\bResult:[ \\t]?(.*?)\\s*</sub-agent>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline
    )]
    private static partial Regex ResultPayload();

    [GeneratedRegex(
        "\\bError:[ \\t]?(.*?)\\s*</sub-agent>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline
    )]
    private static partial Regex ErrorPayload();
}
