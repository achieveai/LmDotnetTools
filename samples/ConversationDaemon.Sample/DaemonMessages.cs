namespace ConversationDaemon.Sample;

/// <summary>
/// User-facing strings the daemon prints. Kept in one place so the wording (in particular the
/// connection-refused guidance) can be asserted directly by the unit tests.
/// </summary>
internal static class DaemonMessages
{
    /// <summary>
    /// Actionable message shown when the daemon cannot open a socket to the LmStreaming.Sample server
    /// (almost always because it is not running). Names the base URL and the command that starts it.
    /// </summary>
    /// <param name="baseUrl">The base URL the daemon tried to reach.</param>
    public static string ConnectionRefused(string baseUrl) =>
        $"Could not reach LmStreaming.Sample at {baseUrl}. "
        + "Start it first (e.g. `dotnet run --project samples/LmStreaming.Sample`), "
        + "then re-run this daemon.";
}
