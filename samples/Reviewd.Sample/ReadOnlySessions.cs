using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reviewd.Sample;

/// <summary>
///     Sandbox sessions created with <c>sandbox create --read-only-egress --read-repos</c>, and the routing that sends
///     a shadow review's steps to one. The gateway does not report a session's network rules back, so creation leaves
///     a marker beside the session secrets on the host, where no sandbox can write it, naming the repos the session
///     may fetch. A shadow run refuses a <c>--post-session</c> without that marker, or one that does not list its repo.
/// </summary>
internal static partial class ReadOnlySessions
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex SessionId();

    private static string MarkerPath(string tokenStoreDir, string sessionId) =>
        SessionId().IsMatch(sessionId)
            ? Path.Combine(tokenStoreDir, "read-only-sessions", sessionId + ".json")
            : throw new ArgumentException($"'{sessionId}' is not a sandbox session id.");

    internal static async Task MarkAsync(
        string tokenStoreDir,
        string sessionId,
        string workspace,
        IReadOnlyList<string> readRepos,
        CancellationToken ct
    )
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
                    readRepos,
                    createdAt = DateTimeOffset.UtcNow,
                }
            ),
            ct
        );
    }

    /// <summary>
    ///     Refuses a <paramref name="sessionId" /> that <c>sandbox create --read-only-egress</c> did not create, and,
    ///     with a <paramref name="repo" />, one whose <c>--read-repos</c> did not list it (its prepare could not fetch).
    /// </summary>
    internal static void Require(string tokenStoreDir, string sessionId, string? repo = null)
    {
        if (!IsMarked(tokenStoreDir, sessionId))
        {
            throw new ArgumentException(
                $"--post-session {sessionId} was not created by sandbox create --read-only-egress on this host."
            );
        }
        if (repo is null)
        {
            return;
        }
        var marker = JsonNode.Parse(File.ReadAllText(MarkerPath(tokenStoreDir, sessionId))) as JsonObject;
        var repos = marker?["readRepos"] as JsonArray;
        if (repos is null || !repos.Any(value => value?.GetValue<string>() == repo))
        {
            throw new ArgumentException(
                $"--post-session {sessionId} cannot fetch {repo}: create it with sandbox create --read-only-egress "
                    + $"--read-repos {repo}."
            );
        }
    }

    /// <summary>True when <c>sandbox create --read-only-egress</c> on this host created <paramref name="sessionId" />.</summary>
    internal static bool IsMarked(string tokenStoreDir, string sessionId) =>
        File.Exists(MarkerPath(tokenStoreDir, sessionId));

    /// <summary>
    ///     Sends every step to <paramref name="postSession" /> when there is one, prepare included, else to
    ///     <paramref name="session" />. The shadow agent can edit the workspace these steps run in, so in a session
    ///     with full egress they could be made to post.
    /// </summary>
    internal static Func<string, JsonObject, string, CancellationToken, Task<JsonObject>> Route(
        Func<string, string, JsonObject, string, CancellationToken, Task<JsonObject>> execute,
        string session,
        string? postSession
    ) => (phase, args, operation, ct) => execute(postSession ?? session, phase, args, operation, ct);
}
