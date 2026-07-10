namespace CodeReviewDaemon.Sample.Configuration;

/// <summary>
/// Extracts the <c>--review &lt;name&gt;</c> profile selector from the process args. The selected name is
/// used as the ASP.NET hosting environment so <c>appsettings.&lt;name&gt;.json</c> is layered over the base
/// config (e.g. <c>--review mcqdb</c> loads <c>appsettings.mcqdb.json</c>). Returns the profile (or
/// <c>null</c> when the flag is absent, or present with no following value) and the remaining args to
/// hand to the host builder with the flag pair stripped out.
/// </summary>
internal static class ReviewProfileArgs
{
    /// <param name="args">Raw process args.</param>
    /// <returns>The selected profile name (null when unset), and the args minus the <c>--review &lt;name&gt;</c> pair.</returns>
    public static (string? Profile, string[] HostArgs) Extract(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var idx = Array.IndexOf(args, "--review");
        if (idx < 0 || idx + 1 >= args.Length)
        {
            return (null, args);
        }

        var profile = args[idx + 1];
        string[] hostArgs = [.. args.Where((_, i) => i != idx && i != idx + 1)];
        return (profile, hostArgs);
    }
}
