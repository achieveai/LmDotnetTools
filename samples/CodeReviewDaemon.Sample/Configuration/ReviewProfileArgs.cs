using System.Globalization;

namespace CodeReviewDaemon.Sample.Configuration;

/// <summary>
/// Extracts the operator command-line flags the daemon consumes before the host builder runs:
/// <list type="bullet">
///   <item><c>--review &lt;name&gt;</c> — the profile selector, used as the ASP.NET hosting environment so
///   <c>appsettings.&lt;name&gt;.json</c> is layered over the base config (e.g. <c>--review mcqdb</c> loads
///   <c>appsettings.mcqdb.json</c>).</item>
///   <item><c>--days &lt;N&gt;</c> (alias <c>--max-pr-age-days &lt;N&gt;</c>) — a per-run override of
///   <see cref="CodeReviewDaemonOptions.MaxPrAgeDays"/>, the "review only PRs active within N days" recency
///   bound. It wins over the profile's configured value.</item>
/// </list>
/// Recognized flag/value pairs are stripped from the returned args before they reach the host builder. A
/// flag that is absent — or present with no following value, or (for the numeric flag) a non-integer value —
/// is ignored and its tokens are left untouched.
/// </summary>
internal static class ReviewProfileArgs
{
    /// <param name="args">Raw process args.</param>
    /// <returns>The selected profile (null when unset), the parsed <c>--days</c> override (null when unset or
    /// malformed), and the args with every recognized flag pair stripped out.</returns>
    public static (string? Profile, int? MaxPrAgeDays, string[] HostArgs) Extract(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var strip = new HashSet<int>();

        var profile = FindValue(args, "--review", strip);

        int? maxPrAgeDays = null;
        foreach (var flag in new[] { "--days", "--max-pr-age-days" })
        {
            var probe = new HashSet<int>();
            var raw = FindValue(args, flag, probe);
            if (raw is not null
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                maxPrAgeDays = parsed;
                strip.UnionWith(probe);
                break;
            }
        }

        string[] hostArgs = [.. args.Where((_, i) => !strip.Contains(i))];
        return (profile, maxPrAgeDays, hostArgs);
    }

    /// <summary>
    /// Finds <paramref name="flag"/> and returns the token that follows it, recording both indices in
    /// <paramref name="strip"/> so the caller can remove the pair. Returns <c>null</c> (recording nothing)
    /// when the flag is absent or is the final token with no value after it.
    /// </summary>
    private static string? FindValue(string[] args, string flag, HashSet<int> strip)
    {
        var idx = Array.IndexOf(args, flag);
        if (idx < 0 || idx + 1 >= args.Length)
        {
            return null;
        }

        strip.Add(idx);
        strip.Add(idx + 1);
        return args[idx + 1];
    }
}
