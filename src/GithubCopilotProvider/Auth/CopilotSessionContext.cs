namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;

/// <summary>
///     Holds the client-tracking identifiers the GitHub Copilot backend correlates requests by.
/// </summary>
/// <remarks>
///     <para>
///     <see cref="MachineId"/> (<c>x-client-machine-id</c>) is stable across runs and is persisted
///     under the user's local application data so the same machine reports a consistent id.
///     </para>
///     <para>
///     <see cref="ClientSessionId"/> (<c>x-client-session-id</c>) is stable for the lifetime of a
///     single provider/agent instance — one id per process session, generated on construction.
///     </para>
///     <para>
///     The per-request <c>x-interaction-id</c> is generated fresh on each request by
///     <see cref="AchieveAi.LmDotnetTools.GithubCopilotProvider.Http.CopilotHeadersHandler"/> and is therefore not
///     stored here.
///     </para>
/// </remarks>
public sealed class CopilotSessionContext
{
    private const string MachineIdFileName = "copilot-machine-id";

    /// <summary>
    ///     Creates a session context. When ids are omitted, the machine id is loaded from (or written
    ///     to) local application data and the client session id is generated fresh.
    /// </summary>
    public CopilotSessionContext(string? machineId = null, string? clientSessionId = null)
    {
        MachineId = !string.IsNullOrWhiteSpace(machineId) ? machineId : LoadOrCreateMachineId();
        ClientSessionId = !string.IsNullOrWhiteSpace(clientSessionId) ? clientSessionId : Guid.NewGuid().ToString();
    }

    /// <summary>Stable, machine-scoped identifier (<c>x-client-machine-id</c>).</summary>
    public string MachineId { get; }

    /// <summary>Per-instance session identifier (<c>x-client-session-id</c>).</summary>
    public string ClientSessionId { get; }

    private static string LoadOrCreateMachineId()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LmDotnetTools"
            );
            var path = Path.Combine(dir, MachineIdFileName);

            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing, out _))
                {
                    return existing;
                }
            }

            var generated = Guid.NewGuid().ToString();
            _ = Directory.CreateDirectory(dir);
            File.WriteAllText(path, generated);
            return generated;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // If the machine id can't be persisted (read-only/sandboxed FS), fall back to an
            // ephemeral id so requests still carry a well-formed header.
            return Guid.NewGuid().ToString();
        }
    }
}
