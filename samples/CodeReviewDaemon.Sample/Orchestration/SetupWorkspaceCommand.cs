using System.Diagnostics;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Bootstraps configured repository worktrees inside the one shared Gateway workspace.</summary>
internal static class SetupWorkspaceCommand
{
    public static void EnsureCanRunWithoutPolling(CodeReviewDaemonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.EnablePrPolling)
            throw new InvalidOperationException(
                "--setup-workspace cannot run with CodeReviewDaemon:EnablePrPolling=true."
            );
    }

    public static async Task RunAsync(
        IReviewSlotPool pool,
        IReviewSessionProvisioner provisioner,
        string storeUrl,
        IReadOnlyList<string> sanitizedArgv,
        string workingDirectory,
        ILogger logger,
        CancellationToken cancellationToken,
        ReviewStore store,
        TimeSpan? operationTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeUrl);
        ArgumentNullException.ThrowIfNull(sanitizedArgv);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        if (pool.Slots.Count == 0)
            throw new InvalidOperationException("Workspace setup requires at least one enabled repository.");

        if (WorkflowWorkspace.HasAnyUnsettledPreparation(store))
            throw new InvalidOperationException(
                "Shared workspace operations remain unsettled; operator reconciliation is required."
            );
        var operationId = store.BeginWorkspaceBootstrap();
        var stopwatch = Stopwatch.StartNew();
        var leased = new List<ReviewSlot>();
        var remoteOutcomeUnknown = false;
        var succeeded = false;
        logger.LogInformation(
            "setup-workspace: starting; argv [{Argv}]; cwd {WorkingDirectory}; slots {SlotCount}.",
            string.Join(' ', sanitizedArgv),
            workingDirectory,
            pool.Slots.Count
        );
        try
        {
            // All addresses are held while shared history/object-store operations run. Unknown outcome for
            // any shared mutation quarantines the whole batch, not only the last individual checkout.
            foreach (var slot in pool.Slots)
                leased.Add(await pool.LeasePreferredAsync(slot, cancellationToken).ConfigureAwait(false));
            var session = await provisioner.GetOrCreateSharedAsync(cancellationToken).ConfigureAwait(false);
            var outerRepository = new ReviewSlotPreparer(
                new Workspace.Git.GitRunner(session.CommandRunner),
                session.FileSystem
            );
            await outerRepository.EnsureWorkspaceRootAsync(storeUrl, cancellationToken).ConfigureAwait(false);
            var scripts = new ReviewSetupScriptRunner(session.CommandRunner, session.FileSystem);
            await scripts.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
            await scripts.SetupRootRepositoriesAsync(cancellationToken, operationTimeout).ConfigureAwait(false);
            foreach (var repository in leased.GroupBy(slot => slot.RepositoryName, StringComparer.Ordinal))
            {
                foreach (var slot in repository)
                {
                    await scripts.EnsureWarmAsync(slot, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "setup-workspace: warmed {SlotName} in shared session {SessionId}.",
                        slot.Name,
                        session.SessionId
                    );
                }
            }
            await scripts.VerifyWarmAsync(leased, cancellationToken).ConfigureAwait(false);
            succeeded = true;
        }
        catch (RemoteWorkspaceOutcomeUnknownException)
        {
            remoteOutcomeUnknown = true;
            throw;
        }
        finally
        {
            if (!remoteOutcomeUnknown)
                store.SettleWorkspaceBootstrap(operationId);
            foreach (var slot in leased)
            {
                if (remoteOutcomeUnknown)
                    await pool.RetireAsync(slot, CancellationToken.None).ConfigureAwait(false);
                else
                    await pool.ReturnAsync(slot, CancellationToken.None).ConfigureAwait(false);
            }
            logger.LogInformation(
                "setup-workspace: finished; outcome {Outcome}; slots {SlotCount}; elapsed {ElapsedMs} ms.",
                succeeded ? "completed"
                    : remoteOutcomeUnknown ? "quarantined"
                    : "failed",
                leased.Count,
                stopwatch.ElapsedMilliseconds
            );
        }
    }
}
