using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reviewd.Sample;

/// <summary>
///     Sandbox sessions created with <c>sandbox create --read-only-egress</c>, and the routing that sends a shadow
///     review's steps to one. The gateway does not report a session's network rules back, so creation leaves a marker
///     beside the session secrets on the host, where no sandbox can write it. A shadow run refuses a
///     <c>--post-session</c> without that marker.
/// </summary>
internal static partial class ReadOnlySessions
{
    /// <summary>
    ///     The steps that keep the full-egress <c>--session</c>: prepare fetches the PR's commits from Azure DevOps over
    ///     git, which needs a POST that read-only egress refuses. Every other step reads or writes only the workspace,
    ///     or GETs from Azure DevOps.
    /// </summary>
    internal static readonly IReadOnlySet<string> FullEgressPhases = new HashSet<string>(StringComparer.Ordinal)
    {
        "prepare",
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex SessionId();

    private static string MarkerPath(string tokenStoreDir, string sessionId) =>
        SessionId().IsMatch(sessionId)
            ? Path.Combine(tokenStoreDir, "read-only-sessions", sessionId + ".json")
            : throw new ArgumentException($"'{sessionId}' is not a sandbox session id.");

    internal static async Task MarkAsync(string tokenStoreDir, string sessionId, string workspace, CancellationToken ct)
    {
        var path = MarkerPath(tokenStoreDir, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    sessionId,
                    workspace,
                    readOnlyEgress = true,
                    createdAt = DateTimeOffset.UtcNow,
                }
            ),
            ct
        );
    }

    /// <summary>Refuses a <paramref name="sessionId" /> that <c>sandbox create --read-only-egress</c> did not create.</summary>
    internal static void Require(string tokenStoreDir, string sessionId)
    {
        if (!File.Exists(MarkerPath(tokenStoreDir, sessionId)))
            throw new ArgumentException(
                $"--post-session {sessionId} was not created by sandbox create --read-only-egress on this host."
            );
    }

    /// <summary>
    ///     Sends <see cref="FullEgressPhases" /> to <paramref name="session" /> and every other step to
    ///     <paramref name="postSession" />. A step-activity or step-stop goes to the session its step runs in, since
    ///     the step's watchdog lives there. With no <paramref name="postSession" /> every step uses
    ///     <paramref name="session" />.
    /// </summary>
    internal static Func<string, JsonObject, string, CancellationToken, Task<JsonObject>> Route(
        Func<string, string, JsonObject, string, CancellationToken, Task<JsonObject>> execute,
        string session,
        string? postSession
    )
    {
        if (postSession is null)
            return (phase, args, operation, ct) => execute(session, phase, args, operation, ct);
        var stepSessions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        return (phase, args, operation, ct) =>
        {
            string target;
            if (phase is "step-activity" or "step-stop")
                target =
                    args["operation"]?.GetValue<string>() is { } step
                    && stepSessions.TryGetValue(step, out var stepSession)
                        ? stepSession
                        : postSession;
            else
            {
                target = FullEgressPhases.Contains(phase) ? session : postSession;
                stepSessions[operation] = target;
            }
            return execute(target, phase, args, operation, ct);
        };
    }
}
