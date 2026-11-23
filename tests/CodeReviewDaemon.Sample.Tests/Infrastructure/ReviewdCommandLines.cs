extern alias Reviewd;

using System.Globalization;
using System.Text;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
///     Reviewd command lines the scenarios run, on workspace <c>workspace</c> and session <c>session</c>.
/// </summary>
internal static class ReviewdCommandLines
{
    /// <summary>
    ///     <c>review reclaim-slot</c> for one PR's slot, with <paramref name="flags"/> such as <c>--execute</c>.
    /// </summary>
    internal static CliOptions ReclaimSlot(string repo, string pr, int slot, string database, params string[] flags) =>
        CliOptions.Parse([
            "review",
            "reclaim-slot",
            "--repo",
            repo,
            "--pr",
            pr,
            "--slot",
            slot.ToString(CultureInfo.InvariantCulture),
            "--db",
            database,
            "--workspace",
            "workspace",
            "--session",
            "session",
            .. flags,
        ]);

    /// <summary>
    ///     The arguments PowerShell passes for a printed command line: words split on spaces, and a single-quoted word
    ///     kept whole with each doubled quote undone. Only the quoting Reviewd prints; a placeholder is refused.
    /// </summary>
    internal static string[] PowerShellArguments(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quoted)
            {
                if (!IsQuote(c))
                {
                    _ = word.Append(c);
                }
                else if (i + 1 < command.Length && IsQuote(command[i + 1]))
                {
                    _ = word.Append(c);
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == ' ')
            {
                if (started)
                {
                    words.Add(word.ToString());
                    _ = word.Clear();
                    started = false;
                }
            }
            else if (c is '<' or '>' or '"' or '$' or '`')
            {
                throw new ArgumentException($"PowerShell would not pass {c} at {i} as part of a value: {command}");
            }
            else
            {
                started = true;
                if (IsQuote(c))
                {
                    quoted = true;
                }
                else
                {
                    _ = word.Append(c);
                }
            }
        }
        if (quoted)
        {
            throw new ArgumentException($"Unclosed quote: {command}");
        }
        if (started)
        {
            words.Add(word.ToString());
        }
        return [.. words];

        // PowerShell reads the typographic single quotes as quotes too.
        static bool IsQuote(char c) => c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B';
    }
}
