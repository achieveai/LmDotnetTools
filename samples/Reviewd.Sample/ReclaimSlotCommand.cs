using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

/// <summary>
/// Reclaims a single review slot held by an idle/dead admitted conversation, by operator request. The
/// reclaim itself — freeze the owning conversation, confirm it quiesced, discard the native slot, then
/// CAS-release the SQLite owner — is the shared <see cref="SlotReaper.ReclaimOneAsync"/> core that the
/// automatic lease reaper (<see cref="SlotReaper.SweepAsync"/>) also uses, so the manual and automatic
/// paths cannot drift. This command targets one explicit slot and never scans by lease.
/// </summary>
internal static class ReclaimSlotCommand
{
    public static async Task<int> RunAsync(CliOptions options, ReviewdConfig config, CancellationToken ct)
    {
        var repo = PrCommands.ParseRepo(options.Required("repo"));
        var pr = options.Required("pr");
        var slot = int.Parse(options.Required("slot"), CultureInfo.InvariantCulture);
        var database = options.Required("db");
        var workspace = options.Required("workspace");
        var session = options.Required("session");
        var execute = options.Has("execute");

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = execute ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        await connection.OpenAsync(ct);

        var (attempt, claimToken, terminal) = await SlotReaper.PrepareAsync(connection, repo, pr, slot, workspace, ct);
        if (terminal is not null)
        {
            // A bad --pr/--slot/--workspace is an operator error worth surfacing loudly; an
            // already-released slot is an idempotent success.
            if (terminal.Status != "already-released")
            {
                throw new InvalidOperationException(
                    terminal.Status switch
                    {
                        "no-attempt" => $"No review attempt for {repo.DisplayName} #{pr}; nothing to reclaim.",
                        "no-claim-token" => "Attempt has no claim token; refusing to reclaim.",
                        _ => "Attempt slot/workspace does not match the request.",
                    }
                );
            }
            Console.WriteLine(JsonSerializer.Serialize(terminal));
            return 0;
        }

        var client = config.CreateS2SClient();
        using var sandboxClient = config.CreateSandboxClient(TimeSpan.FromMinutes(10));
        SlotReaper.SandboxExecutor sandbox = (phase, args, operation, token) =>
            ReviewRunner.ExecuteSandboxAsync(sandboxClient, session, phase, args, operation, token);

        // Forces through native records that disagree with ours exactly when the reaper would.
        var force = await SlotReaper.IsForceDueAsync(
            connection,
            workspace,
            repo,
            slot,
            claimToken!,
            DateTimeOffset.UtcNow,
            ct
        );
        var result = await SlotReaper.ReclaimOneAsync(
            connection,
            client,
            sandbox,
            repo,
            pr,
            slot,
            workspace,
            attempt!,
            claimToken!,
            execute,
            ct,
            force
        );
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }
}
