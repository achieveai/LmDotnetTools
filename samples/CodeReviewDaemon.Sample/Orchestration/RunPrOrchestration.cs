using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Task #81, round 4, items 2+3 — the exact prepare → lease → host-start → admit+run → host-stop sequencing
/// <c>Program.cs</c>'s <c>--run-pr</c> arm needs, pulled out of Program.cs's top-level statements so it can
/// be proven by a composition test with fake delegates: no live provider, no live ASP.NET host, no real
/// SQLite lease file. Every step is injected rather than hardcoded, so a test can assert what Program.cs
/// itself cannot easily prove — see <see cref="ExecuteAsync"/>'s remarks for exactly what each step
/// guarantees about the ones after it.
/// </summary>
internal static class RunPrOrchestration
{
    internal sealed record Request(string RepoKey, string PrId, string HeadSha, string BaseSha);

    /// <summary>
    /// Runs explicitly supplied requests in one coordinator. Fresh requests wait for an atomic repository
    /// slot claim; already-active PRs are refused without invoking another review. EOF drains active work.
    /// A failure stops new admissions without cancelling already accepted hosted work.
    /// </summary>
    public static async Task<int> ExecuteStreamAsync(
        TextReader input,
        int concurrency,
        Func<RunPrCoordinatorLeaseOutcome> acquireLease,
        Func<Task> startHostAsync,
        Func<CancellationToken, Task> recoverAsync,
        Func<Request, string?> repositoryFor,
        Func<Request, (bool Active, ReviewSlot? Preferred)> assignmentFor,
        ReviewSlotPool slotPool,
        ILogger logger,
        Func<Request, ReviewSlotClaim?, CancellationToken, Task<RunSinglePrResult>> runAsync,
        Action<object> report,
        Func<Task> stopHostAsync,
        CancellationToken cancellationToken
    )
    {
        if (concurrency is < 1 or > 50 || concurrency > slotPool.Slots.Count)
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        ArgumentNullException.ThrowIfNull(recoverAsync);
        ArgumentNullException.ThrowIfNull(repositoryFor);
        ArgumentNullException.ThrowIfNull(assignmentFor);
        ArgumentNullException.ThrowIfNull(logger);
        using var lease = acquireLease();
        if (!lease.IsAcquired)
        {
            report(new { Event = "rejected", Reason = "CoordinatorLeaseUnavailable" });
            return 1;
        }
        await startHostAsync().ConfigureAwait(false);
        var active = new List<Task<bool>>();
        var pending = new List<Request>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = false;
        try
        {
            await recoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stopHostAsync().ConfigureAwait(false);
            throw;
        }
        try
        {
            report(
                new
                {
                    Event = "ready",
                    Concurrency = concurrency,
                    QueuedRequests = 0,
                }
            );
            Task<string?>? pendingLine = null;
            var stopping = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var eof = false;
            while (!failed)
            {
                foreach (var completed in active.Where(task => task.IsCompleted).ToArray())
                {
                    failed |= !await completed.ConfigureAwait(false);
                    active.Remove(completed);
                }
                if (failed || cancellationToken.IsCancellationRequested)
                {
                    failed = true;
                    break;
                }

                foreach (var candidate in pending.ToArray())
                {
                    if (active.Count >= concurrency)
                        break;
                    var repository = repositoryFor(candidate);
                    if (repository is null)
                    {
                        report(new { Event = "rejected", Reason = "RepositoryOutsideAllowList" });
                        failed = true;
                        break;
                    }
                    var (owner, preferred) = assignmentFor(candidate);
                    if (owner)
                    {
                        pending.Remove(candidate);
                        report(
                            new
                            {
                                Event = "rejected",
                                Reason = "AlreadyActive",
                                candidate.PrId,
                            }
                        );
                        failed = true;
                        break;
                    }
                    ReviewSlotClaim? claim = slotPool.TryClaim(repository, preferred);
                    if (claim is null)
                    {
                        logger.LogDebug(
                            "Stream request waits for repository slot {Repository} {PrId}",
                            repository,
                            candidate.PrId
                        );
                        continue;
                    }
                    pending.Remove(candidate);
                    logger.LogDebug(
                        "Stream request dispatched {Repository} {PrId} {SlotName} {ExistingOwner} {PendingCount}",
                        repository,
                        candidate.PrId,
                        claim?.Slot.Name,
                        false,
                        pending.Count
                    );
                    active.Add(RunOneAsync(candidate, claim));
                }
                if (failed)
                    break;
                if (eof && active.Count == 0)
                {
                    if (pending.Count > 0)
                    {
                        report(
                            new
                            {
                                Event = "rejected",
                                Reason = "RepositoryCapacityUnavailable",
                                QueuedRequests = pending.Count,
                            }
                        );
                        failed = true;
                    }
                    break;
                }
                if (eof && pending.Count == 0)
                {
                    if (await Task.WhenAny(active.Cast<Task>().Append(stopping)).ConfigureAwait(false) == stopping)
                        failed = true;
                    continue;
                }
                // Bounded read-ahead allows another repository to fill its free slots when one is busy.
                // Console.In may implement async reads synchronously, so keep it off the review loop.
                if (!eof && pendingLine is null)
                    pendingLine = Task.Run(async () =>
                        await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    );
                var ready = await Task.WhenAny(
                        active.Cast<Task>().Concat(pendingLine is null ? [] : [pendingLine]).Append(stopping)
                    )
                    .ConfigureAwait(false);
                if (ready == stopping)
                {
                    failed = true;
                    break;
                }
                if (pendingLine is null || !pendingLine.IsCompleted)
                    continue;
                var line = await pendingLine.ConfigureAwait(false);
                pendingLine = null;
                if (line is null)
                {
                    eof = true;
                    continue;
                }
                Request? request = null;
                try
                {
                    if (line.Length <= 4096)
                        request = System.Text.Json.JsonSerializer.Deserialize<Request>(line);
                }
                catch (System.Text.Json.JsonException) { }
                var repositoryKey = request is null ? null : repositoryFor(request);
                if (
                    request is null
                    || repositoryKey is null
                    || !int.TryParse(request.PrId, out var prId)
                    || prId <= 0
                    || request.HeadSha is not { Length: 40 }
                    || !request.HeadSha.All(Uri.IsHexDigit)
                    || request.BaseSha is not { Length: 40 }
                    || !request.BaseSha.All(Uri.IsHexDigit)
                    || seen.Count >= 50
                    || !seen.Add(repositoryKey + ":" + prId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                )
                {
                    report(new { Event = "rejected", Reason = "InvalidDuplicateOrOverCapacityRequest" });
                    failed = true;
                    break;
                }
                pending.Add(request with { PrId = prId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                logger.LogDebug(
                    "Stream request queued {Repository} {PrId} {PendingCount}",
                    repositoryKey,
                    prId,
                    pending.Count
                );
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failed = true;
        }
        finally
        {
            // Do not stop the callback host while sibling workflows still need it.
            try
            {
                foreach (var result in await Task.WhenAll(active).ConfigureAwait(false))
                    failed |= !result;
                if (pending.Count > 0)
                    failed = true;
                logger.LogDebug("Stream drained {Success} {PendingCount}", !failed, pending.Count);
            }
            finally
            {
                await stopHostAsync().ConfigureAwait(false);
            }
        }
        report(new { Event = "drained", Success = !failed });
        return failed ? 1 : 0;

        async Task<bool> RunOneAsync(Request request, ReviewSlotClaim? claim)
        {
            try
            {
                report(
                    new
                    {
                        Event = "started",
                        request.RepoKey,
                        request.PrId,
                    }
                );
                var result = await runAsync(request, claim, cancellationToken).ConfigureAwait(false);
                var succeeded = result.Admitted && result.WorkflowStatus == Persistence.Models.WorkflowStatus.Completed;
                report(
                    new
                    {
                        Event = "finished",
                        request.PrId,
                        Success = succeeded,
                        Result = result,
                    }
                );
                return succeeded;
            }
            catch (Exception exception)
            {
                // Provider exception messages can contain credentials. Only expose the exception type.
                report(
                    new
                    {
                        Event = "failed",
                        request.PrId,
                        ErrorType = exception.GetType().Name,
                    }
                );
                return false;
            }
            finally
            {
                if (claim is not null)
                    await claim.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs one <c>--run-pr</c> attempt through every stage, in order:
    /// <list type="number">
    /// <item><paramref name="prepareAsync"/> — host-independent pre-admission checks. A rejection here
    /// returns immediately; NEITHER <paramref name="acquireLease"/> NOR <paramref name="startHostAsync"/> is
    /// ever called for a run that was always going to be refused.</item>
    /// <item><paramref name="acquireLease"/> — the fail-closed OS-level coordinator lease gate (security
    /// review round 1). Lease refusal returns a <see cref="RunSinglePrRejectionReason.CoordinatorLeaseUnavailable"/>
    /// rejection; <paramref name="startHostAsync"/> is never called in this case either — a lease that could
    /// not be acquired must never let the host come up at all.</item>
    /// <item><paramref name="startHostAsync"/> — started only once both gates above passed. Deliberately
    /// OUTSIDE the try/finally below: if starting the host itself throws, there is nothing to stop.</item>
    /// <item><paramref name="recoverAsync"/> — restores existing active workspace owners under the coordinator
    /// lease before a fresh run can claim a slot. A failure stops the host without admitting work.</item>
    /// <item><paramref name="admitAndRunAsync"/> — the actual admission + orchestrator run, wrapped so that
    /// EITHER a graceful cancellation (round 4, item 2 — the run's own <paramref name="cancellationToken"/>,
    /// e.g. Ctrl+C/application-stopping, firing mid-run) OR any other exception still reaches the
    /// <c>finally</c> below before propagating/returning.</item>
    /// <item><paramref name="stopHostAsync"/> — always runs once <paramref name="startHostAsync"/> has run,
    /// on every exit from step 4: success, a thrown exception (rethrown after this runs), or a graceful
    /// cancellation (reported as <see cref="RunSinglePrRejectionReason.Cancelled"/> rather than rethrown,
    /// since Ctrl+C during an operator-approved one-shot run is an ordinary, expected shutdown — not a
    /// failure the caller needs to see as an unhandled exception).</item>
    /// </list>
    /// </summary>
    public static async Task<RunSinglePrResult> ExecuteAsync(
        Func<CancellationToken, Task<RunSinglePrPrepareResult>> prepareAsync,
        Func<RunPrCoordinatorLeaseOutcome> acquireLease,
        Func<Task> startHostAsync,
        Func<CancellationToken, Task> recoverAsync,
        Func<RunSinglePrPrepareResult, CancellationToken, Task<RunSinglePrResult>> admitAndRunAsync,
        Func<Task> stopHostAsync,
        CancellationToken cancellationToken
    )
    {
        var prepared = await prepareAsync(cancellationToken).ConfigureAwait(false);
        if (!prepared.Accepted)
        {
            return RunSinglePrResult.Rejected(prepared.RejectionReason!.Value);
        }

        using var lease = acquireLease();
        if (!lease.IsAcquired)
        {
            return RunSinglePrResult.Rejected(RunSinglePrRejectionReason.CoordinatorLeaseUnavailable);
        }

        await startHostAsync().ConfigureAwait(false);
        try
        {
            await recoverAsync(cancellationToken).ConfigureAwait(false);
            return await admitAndRunAsync(prepared, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RunSinglePrResult.Rejected(RunSinglePrRejectionReason.Cancelled);
        }
        finally
        {
            await stopHostAsync().ConfigureAwait(false);
        }
    }
}
