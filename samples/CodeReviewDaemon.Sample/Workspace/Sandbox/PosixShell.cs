using System.Text;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Builds a safe POSIX shell command line from an argument vector. The sandbox gateway exposes a
/// <c>Bash</c> tool that takes a single command string, but the daemon's git arguments include
/// attacker-influenced values (branch names, paths, submodule URLs). Single-quote escaping every
/// token guarantees those values are passed literally and can never break out into shell
/// metacharacters, command substitution, or argument injection.
/// </summary>
internal static class PosixShell
{
    /// <summary>
    /// Quotes a single argument for POSIX <c>sh</c>/<c>bash</c> using the canonical single-quote
    /// technique: wrap in <c>'…'</c> and rewrite each embedded <c>'</c> as <c>'\''</c>. An empty
    /// string becomes <c>''</c>.
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length == 0)
        {
            return "''";
        }

        var builder = new StringBuilder(argument.Length + 2);
        _ = builder.Append('\'');
        foreach (var ch in argument)
        {
            if (ch == '\'')
            {
                _ = builder.Append("'\\''");
            }
            else
            {
                _ = builder.Append(ch);
            }
        }

        _ = builder.Append('\'');
        return builder.ToString();
    }

    /// <summary>
    /// Renders <paramref name="command"/> as a single safely-quoted shell line. When a working
    /// directory is set, the line is <c>cd -- '&lt;dir&gt;' &amp;&amp; &lt;argv&gt;</c> so the
    /// command fails fast (rather than running in the wrong place) if the directory is missing.
    /// </summary>
    public static string BuildCommandLine(SandboxCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Argv.Count == 0)
        {
            throw new ArgumentException("Argv must contain at least the executable.", nameof(command));
        }

        var quotedArgv = string.Join(' ', command.Argv.Select(Quote));

        if (string.IsNullOrEmpty(command.WorkingDirectory))
        {
            return quotedArgv;
        }

        return $"cd -- {Quote(command.WorkingDirectory)} && {quotedArgv}";
    }
}
