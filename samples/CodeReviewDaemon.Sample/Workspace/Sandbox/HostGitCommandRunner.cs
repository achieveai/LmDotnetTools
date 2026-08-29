using System.Diagnostics;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Runs deterministic git/fs commands as HOST processes (design §6): the daemon's retention push lives
/// OUTSIDE the sandbox, so the untrusted review agent's tools — which run inside the sandbox — can never
/// share the write credential. A git command that talks to a remote gets the credential injected via
/// <see cref="HostGitCredentialEnv"/> (token off argv + off on-disk config).
/// <para>
/// Every command is BOUNDED, and one that talks to a remote is AUDIBLE. Neither used to be true: a
/// 969,911-object fetch into a 2.3 GB store ran with no deadline of any kind and emitted not one line
/// while it worked, so the daemon was indistinguishable from dead and was twice diagnosed as hung while
/// it was in fact making progress. The sandbox side has bounded every command at five minutes since it
/// was written; only this side was unbounded, which reads as an oversight rather than a decision.
/// </para>
/// </summary>
internal sealed class HostGitCommandRunner : ISandboxCommandRunner
{
    /// <summary>
    /// Git verbs that talk to a remote. Only these are announced and only these get <c>--progress</c>:
    /// they are the only commands that can take minutes, and they are a rounding error in the volume of
    /// git the daemon runs. Announcing <c>rev-parse</c> as well would bury the one line worth reading.
    /// </summary>
    private static readonly HashSet<string> S_remoteVerbs = new(StringComparer.Ordinal)
    {
        "fetch",
        "clone",
        "push",
        "pull",
        "ls-remote",
        "submodule",
    };

    /// <summary>
    /// Remote verbs that accept <c>--progress</c>. <c>ls-remote</c> talks to a remote but has no such flag,
    /// so it is announced without one.
    /// <para>
    /// <c>submodule</c> is here for the reason the whole idle-timeout mechanism exists. <c>git submodule
    /// update</c> emits NOTHING on a redirected stderr unless asked, so the watchdog saw pure silence and
    /// killed healthy multi-gigabyte fetches at exactly the idle timeout — observed twice in fifteen
    /// minutes at 300.2s and 300.3s, each kill abandoning a pack (4.68 GB, 5.85 GB, 0.70 GB). That retry
    /// loop is how 35 orphans reached 245 GB and took the filesystem read-only. The verb was bounded like a
    /// command that reports progress while being run like one that cannot.
    /// </para>
    /// <para>
    /// Note that <c>--progress</c> is NOT valid where this class would naively put it. <c>git submodule
    /// --progress update</c> is rejected with a usage dump; the flag belongs to the SUBCOMMAND, and git
    /// 2.53.0's own usage string does not document it on <c>update</c> at all even though it is accepted
    /// (verified against a positive control). See <c>ProgressAnchorIndex</c>.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> S_progressVerbs = new(StringComparer.Ordinal)
    {
        "fetch",
        "clone",
        "push",
        "pull",
        "submodule",
    };

    /// <summary>
    /// Verbs that write a pack into the LOCAL object store, and so can abandon a <c>tmp_pack_*</c> if they
    /// are killed mid-write. <c>push</c> is deliberately absent: it streams its pack to the remote and
    /// leaves nothing behind here. <c>submodule</c> IS present even though it writes no pack itself — the
    /// <c>git fetch</c> it spawns writes one per submodule, and every orphan actually measured on this
    /// machine came from a killed <c>submodule update</c> rather than from a direct fetch. Scoping this set
    /// to verbs that write packs *themselves* is a narrower rule than the failure it has to cover.
    /// See <see cref="OrphanedPackSweeper"/>.
    /// </summary>
    private static readonly HashSet<string> S_packWritingVerbs = new(StringComparer.Ordinal)
    {
        "fetch",
        "clone",
        "pull",
        "submodule",
    };

    /// <summary>
    /// Verbs whose silence carries NO information, so the idle timeout must not be applied to them.
    /// <para>
    /// This is not a tuning allowance, it is a correction to a false premise. The idle timeout exists
    /// because "a healthy remote command keeps talking" — true of <c>fetch</c>, which is asked for
    /// <c>--progress</c> and answers. It is false of <c>submodule update</c>, and asking it for
    /// <c>--progress</c> does NOT make it true: measured in production, git's own
    /// <c>git submodule--helper</c> spawns its child as <c>git fetch origin &lt;sha&gt;</c> with no progress
    /// flag propagated, so the child is silent on a redirected stderr no matter what the parent was asked.
    /// </para>
    /// <para>
    /// The cost of pretending otherwise was measured too: a submodule fetch that had already written
    /// <b>8.1 GB</b> and was still going was killed at 365.6s for "no output for 300s", abandoning that pack.
    /// Retry, repeat — that loop is how 35 orphans reached 245 GB and forced a read-only remount. These
    /// commands stay bounded by the duration ceiling, which is the honest bound for something that cannot be
    /// watched.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> S_silentVerbs = new(StringComparer.Ordinal) { "submodule" };

    /// <summary>
    /// How often the watchdog re-checks the deadlines. Fine enough to kill promptly, coarse enough to cost
    /// nothing next to a multi-minute fetch.
    /// </summary>
    private static readonly TimeSpan S_watchdogTick = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How often a still-running remote operation reports progress. An eleven-minute fetch produces roughly
    /// twenty lines — a bargain against the two wrong "it's hung" diagnoses the silence caused — while
    /// anything finishing inside one interval stays down to a start and a finish.
    /// </summary>
    private static readonly TimeSpan S_progressInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the post-exit drain may run before the capture is declared incomplete. Separate from the
    /// watchdog on purpose: the watchdog bounds a command that is still RUNNING, this bounds one that has
    /// already FINISHED. Collapsing them would make a five-minute idle timeout the bound on a drain that
    /// should take microseconds. Five seconds is generous by orders of magnitude precisely so that hitting
    /// it means something is genuinely wrong rather than merely slow.
    /// </summary>
    private static readonly TimeSpan S_drainDeadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the sweep may wait for a killed tree to actually let go of the packs it was writing.
    /// <para>
    /// This exists because <c>Kill(entireProcessTree: true)</c> only DELIVERS signals. The descriptor
    /// <c>index-pack</c> holds on its <c>tmp_pack_*</c> is released as the kernel tears that process down,
    /// which is not synchronous with the signal and is not covered by waiting on our own direct child —
    /// <c>index-pack</c> is a grandchild, reparented to init and reaped there. Sweeping on the next line
    /// therefore samples <c>/proc</c> while the writer is still visible, spares the file it was created to
    /// remove, and logs nothing: the multi-gigabyte leak survives, and the log reads exactly like the guard
    /// working. The test fixture for this file has always waited here; production did not, and that gap is
    /// the whole of the defect.
    /// </para>
    /// <para>
    /// Bounded, and generously: five seconds is orders of magnitude more than a reap takes, so reaching it
    /// means something is genuinely wrong rather than merely slow. On expiry the sweep still runs — the
    /// open-handle guard is what makes that safe, and it is consulted again inside the sweep — but the
    /// outcome is reported rather than silently accepted. The cost is paid only on a kill, and the one path
    /// where it is felt is shutdown, which is bounded by the same five seconds.
    /// </para>
    /// </summary>
    private static readonly TimeSpan S_reapDeadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the reap wait re-checks. Each check that finds a candidate walks the readable descriptor
    /// tables, so this is coarse enough not to spin on that and fine enough that the common case — already
    /// reaped — costs one poll.
    /// </summary>
    private static readonly TimeSpan S_reapPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Func<CancellationToken, Task<IReadOnlyList<GitProviderToken>>> _credentialsSource;
    private readonly ILogger<HostGitCommandRunner> _logger;
    private readonly IReadOnlyCollection<string>? _adoOrgs;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _maxDuration;

    /// <param name="credentialsSource">Signed-in provider tokens, injected per command via the environment.</param>
    /// <param name="logger">Where the start/progress/finish of a remote operation is reported.</param>
    /// <param name="adoOrgs">ADO orgs whose hosts need an auth header.</param>
    /// <param name="idleTimeout">
    /// How long a command may produce NO output before it is presumed stuck and killed. Inactivity rather
    /// than elapsed time is the bound, because elapsed time cannot tell the two apart: the live fetch was
    /// healthy eleven minutes in, so a ceiling loose enough to permit it is useless as a stuck-detector,
    /// while one tight enough to catch stuck kills honest work on a large store. A real stall looks like
    /// <c>curl 56 Recv failure: Connection timed out</c> — silence, not slowness. Defaults to five minutes,
    /// matching the sandbox side's per-command timeout.
    /// </param>
    /// <param name="maxDuration">
    /// Backstop for the one case inactivity cannot catch: a remote that dribbles bytes forever, never
    /// silent and never finished.
    /// </param>
    public HostGitCommandRunner(
        Func<CancellationToken, Task<IReadOnlyList<GitProviderToken>>> credentialsSource,
        ILogger<HostGitCommandRunner> logger,
        IReadOnlyCollection<string>? adoOrgs = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? maxDuration = null
    )
    {
        _credentialsSource = credentialsSource ?? throw new ArgumentNullException(nameof(credentialsSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _adoOrgs = adoOrgs;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        _maxDuration = maxDuration ?? TimeSpan.FromMinutes(60);
    }

    public async Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Argv.Count == 0)
        {
            throw new ArgumentException("Argv must be non-empty.", nameof(command));
        }

        // Process.Start throws Win32Exception(267) if WorkingDirectory is set but doesn't exist yet — e.g.
        // the sweeper's first-ever probe of a checkout dir that hasn't been cloned. Fail gracefully instead
        // of crashing so callers like ReviewBotCheckout can fall through from "probe" to "clone" (which
        // creates the directory) rather than aborting the whole sweep.
        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory) && !Directory.Exists(command.WorkingDirectory))
        {
            var missingDirResult = new SandboxCommandResult(
                1,
                string.Empty,
                $"working directory '{command.WorkingDirectory}' does not exist"
            );
            _logger.LogDebug(
                "Host git '{Argv}' exited {Exit}: {Stderr}",
                string.Join(' ', command.Argv),
                missingDirResult.ExitCode,
                missingDirResult.Stderr
            );
            return missingDirResult;
        }

        var isGit = string.Equals(command.Argv[0], "git", StringComparison.OrdinalIgnoreCase);
        var verb = isGit ? GitVerb(command.Argv) : null;
        var isRemote = verb is not null && S_remoteVerbs.Contains(verb);

        // --progress is added HERE rather than at the call sites, for the same reason the hardening flags
        // are centralized: no call site can then forget it. It is not cosmetic. Redirecting stderr means it
        // is not a TTY, and git emits no progress at all in that case unless explicitly asked — so without
        // this there is no progress being discarded, there is none being produced, and any fix that only
        // surfaced existing output would have changed nothing while looking correct.
        var argv = isRemote && S_progressVerbs.Contains(verb!) ? WithProgress(command.Argv, verb!) : command.Argv;

        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        for (var i = 1; i < argv.Count; i++)
        {
            psi.ArgumentList.Add(argv[i]);
        }

        // Inject each signed-in provider's credential only when this is a git command (the sole
        // remote-talking case here) — GitHub and/or Azure DevOps, per which providers are signed in.
        // HostGitCredentialEnv always sets GIT_TERMINAL_PROMPT=0, so even a credential-less git command
        // fails fast rather than hanging on a prompt.
        if (isGit)
        {
            var credentials = await _credentialsSource(cancellationToken).ConfigureAwait(false);
            foreach (var (k, v) in HostGitCredentialEnv.Build(credentials, _adoOrgs))
            {
                psi.Environment[k] = v;
            }
        }

        var started = Stopwatch.StartNew();
        if (isRemote)
        {
            _logger.LogInformation(
                "Host git {Verb} starting in '{WorkingDirectory}' (idle timeout {IdleSeconds}s, "
                    + "ceiling {CeilingMinutes}m).",
                verb,
                command.WorkingDirectory,
                _idleTimeout.TotalSeconds,
                _maxDuration.TotalMinutes
            );
        }

        // Taken BEFORE the write begins, because after the kill there is no way to tell our abandoned pack
        // from one that was already lying there. An age threshold cannot do this job: the file we are about
        // to orphan is the YOUNGEST in the directory, so any "older than N" rule sweeps exactly nothing on
        // the one path that matters. Null for every verb that cannot write a local pack, which is almost
        // all of them, so the ordinary git command pays one set lookup and no I/O.
        var preexistingTempPacks =
            verb is not null && S_packWritingVerbs.Contains(verb)
                ? OrphanedPackSweeper.Snapshot(command.WorkingDirectory)
                : null;

        using var process = new Process { StartInfo = psi };
        _ = process.Start();

        // A timeout is OUR decision and stays distinguishable from the caller's cancellation: a cancelled
        // caller is the daemon shutting down (nobody is waiting for the answer, so rethrow), whereas a
        // timeout is a failed operation the run should retry. Both kill the child.
        using var timeoutCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var activity = new ActivityClock();
        var stdout = ReadStreamAsync(process.StandardOutput, activity, null, linked.Token);
        var stderr = ReadStreamAsync(process.StandardError, activity, isRemote ? verb : null, linked.Token);

        // A verb that cannot report progress cannot be judged by its silence. Applying the idle timeout to
        // one killed healthy multi-gigabyte fetches on schedule; the duration ceiling still bounds it.
        var idleTimeout = verb is not null && S_silentVerbs.Contains(verb) ? _maxDuration : _idleTimeout;
        var watchdog = WatchdogAsync(activity, started, idleTimeout, timeoutCts, linked.Token);

        string capturedStdout;
        string capturedStderr;
        bool captureComplete;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            // Drain BOTH streams to their natural EOF while `linked` is still live. This ordering IS the
            // fix. The finally below cancels the very token these readers are reading on, so collecting
            // their results after it — which is what this method used to do — handed back whatever had
            // been accumulated when the cancellation landed. ReadStreamAsync treats cancellation as
            // "return what I have", so the loss was silent and could be total: exit code 0, empty stdout,
            // empty stderr, on a command that had in fact succeeded and printed. Measured in production
            // (run 200, 2026-08-08T02:13:18Z: `git rev-parse HEAD` exit 0 with no output, which no git
            // invocation produces) and reproduced at 85/300 under concurrency. A fast command is the worst
            // case, because the process can exit before the reader task has been scheduled at all.
            //
            // BOUNDED, because "the process has exited so both pipes are at EOF" is FALSE in general — and
            // an earlier version of this comment asserted it. A pipe reaches EOF when the LAST write handle
            // closes, so anything git spawned that outlives it while holding the inherited handle keeps our
            // read pending. Unbounded here would trade a silent truncation for a silent HANG, on a path the
            // daemon runs constantly — strictly worse, and this fix would have introduced it while looking
            // like pure hardening.
            //
            // Two things were MEASURED rather than assumed, because the obvious candidate turned out not to
            // be one. `gc --auto` does NOT hold this pipe: `gc.autoDetach` defaults to true (git 2.53.0's
            // own docs) and a detaching maintenance run redirects its stdio to .git/gc.log, so it never
            // inherits ours. The realistic holders are helpers git normally WAITS for — ssh, credential
            // helpers, hooks, external diff drivers — which matters only if one of them backgrounds
            // something itself. And nothing here needs to kill the holder: once this method returns and the
            // Process is disposed our read end closes, and a holder that writes afterwards takes SIGPIPE on
            // that write (verified). So the deadline is about not blocking the daemon; the holder's fate is
            // already decided by the pipe, not by us.
            captureComplete =
                await DrainWithinDeadlineAsync(stdout, stderr, timeoutCts).ConfigureAwait(false)
                && !linked.Token.IsCancellationRequested;
            capturedStdout = await SafeResultAsync(stdout).ConfigureAwait(false);
            capturedStderr = await SafeResultAsync(stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);

            // The kill only delivers the signal; the pack descriptor is released as the tree is torn down.
            // Sweeping without waiting for that finds the dying writer still holding the file, spares it,
            // and leaks the gigabytes this whole class exists to reclaim. See S_reapDeadline. Skipped
            // entirely when no snapshot was taken, so a killed `rev-parse` still costs nothing.
            var (settled, waited) = preexistingTempPacks is null
                ? (true, TimeSpan.Zero)
                : await WaitForKilledTreeAsync(process, command.WorkingDirectory, preexistingTempPacks)
                    .ConfigureAwait(false);

            // Both exits from here abandon a half-written pack, so both sweep. Ordered before the rethrow
            // deliberately: shutdown mid-fetch is not the rare case it reads as — it is how the daemon stops
            // every time, and skipping cleanup there would leak on the most common kill of all.
            SweepAbandonedPacks(command.WorkingDirectory, preexistingTempPacks, verb ?? argv[0], settled, waited);

            // The caller's cancellation wins: on shutdown nobody wants a result, and reporting one as a
            // failed run would mark every in-flight review RetryPending on every stop.
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            var reason = activity.TimedOutOnCeiling
                ? $"exceeded the {_maxDuration.TotalMinutes:0.##}m ceiling"
                : $"produced no output for {idleTimeout.TotalSeconds:0.##}s (idle timeout)";
            _logger.LogWarning(
                "Host git {Verb} in '{WorkingDirectory}' {Reason} after {ElapsedSeconds:0.#}s and was killed.",
                verb ?? argv[0],
                command.WorkingDirectory,
                reason,
                started.Elapsed.TotalSeconds
            );

            // Returned, not thrown, so this travels the path a genuine network failure already takes:
            // RunGitOrThrowAsync turns a non-zero result into an InvalidOperationException, which the
            // orchestrator records as RetryPending. No new failure shape reaches any caller. 124 is the
            // conventional "killed by a timeout" exit code.
            //
            // Cancelling the readers is CORRECT here and stays: the child was killed, a partial capture is
            // the best evidence available, and exit 124 already tells the caller not to trust it as a
            // complete answer. That is exactly what distinguishes this path from the success path above.
            return new SandboxCommandResult(
                124,
                await SafeResultAsync(stdout).ConfigureAwait(false),
                $"git {verb ?? argv[0]} {reason} and was killed by the daemon after "
                    + $"{started.Elapsed.TotalSeconds:0.#}s."
            );
        }
        finally
        {
            await timeoutCts.CancelAsync().ConfigureAwait(false);
            await SafeAwaitAsync(watchdog).ConfigureAwait(false);
        }

        // The third distinguishable outcome. A complete capture returns the child's own exit code; a
        // timeout-kill returns 124 with whatever partial output existed; this returns 125 and says why.
        // It is reported as a FAILURE rather than as a short answer on purpose: once a write handle
        // outlives the child, there is no way to know whether what was read is all there was, and
        // "possibly truncated" reported as success is precisely the defect this whole ticket is about.
        if (!captureComplete)
        {
            _logger.LogWarning(
                "Host git {Verb} in '{WorkingDirectory}' exited {Exit} after {ElapsedSeconds:0.#}s, but its "
                    + "output pipes were still open {DrainSeconds:0.##}s later — something it spawned still "
                    + "holds them. The capture is INCOMPLETE and is being failed rather than returned short.",
                verb ?? argv[0],
                command.WorkingDirectory,
                process.ExitCode,
                started.Elapsed.TotalSeconds,
                S_drainDeadline.TotalSeconds
            );

            // NOT swept, and that is a decision rather than an omission. 125 is the one outcome where a
            // descendant of this command is known to be ALIVE — that is what the exit code MEANS — so the
            // sweep would be running against a live process by construction. The open-handle guard covers
            // most of that, but not the window between `index-pack` closing its finished temp pack and
            // renaming it into place: for those microseconds the file has no holder and is absent from the
            // snapshot, so it reads as an orphan and would be deleted after it had been written
            // successfully. Against that the expected gain is close to zero — git itself has already
            // exited here, so its pack was either renamed or cleaned up, and the realistic pipe-holder is a
            // helper git waits for (ssh, a credential helper, a hook) rather than something writing a pack.
            // A narrow sweep that misses a rare orphan beats a broad one that can corrupt a good fetch.
            return new SandboxCommandResult(
                125,
                capturedStdout,
                $"git {verb ?? argv[0]} exited {process.ExitCode}, but its output could not be drained within "
                    + $"{S_drainDeadline.TotalSeconds:0.##}s because a surviving child still holds the pipe. "
                    + "The captured output is incomplete and must not be treated as the command's answer."
            );
        }

        var result = new SandboxCommandResult(process.ExitCode, capturedStdout, capturedStderr);

        if (isRemote)
        {
            _logger.LogInformation(
                "Host git {Verb} in '{WorkingDirectory}' finished with exit {Exit} after {ElapsedSeconds:0.#}s.",
                verb,
                command.WorkingDirectory,
                result.ExitCode,
                started.Elapsed.TotalSeconds
            );
        }

        if (!result.Succeeded)
        {
            _logger.LogDebug(
                "Host git '{Argv}' exited {Exit}: {Stderr}",
                string.Join(' ', argv),
                result.ExitCode,
                result.Stderr
            );
        }

        return result;
    }

    /// <summary>
    /// Whether a line is git's own progress chatter rather than something a human would want in a failure
    /// report. Keyed on the <c>NN% (n/m)</c> shape every git progress counter uses
    /// (<c>Receiving objects</c>, <c>Resolving deltas</c>, <c>remote: Counting objects</c>), which no git
    /// error message carries. Deliberately narrow: a filter loose enough to also swallow
    /// <c>fatal: could not read from remote repository</c> or
    /// <c>error: RPC failed; curl 56 Recv failure</c> would hide precisely the lines a stalled fetch is
    /// diagnosed from, which is a worse outcome than the noise it removes.
    /// </summary>
    internal static bool IsProgressLine(string line) => line.Contains("% (", StringComparison.Ordinal);

    /// <summary>
    /// Inserts <c>--progress</c> immediately after the verb, unless the caller already asked for it or
    /// explicitly suppressed it with <c>--no-progress</c>.
    /// </summary>
    internal static IReadOnlyList<string> WithProgress(IReadOnlyList<string> argv, string verb)
    {
        if (argv.Contains("--progress") || argv.Contains("--no-progress"))
        {
            return argv;
        }

        var anchor = ProgressAnchorIndex(argv, verb);
        if (anchor < 0)
        {
            return argv;
        }

        var withProgress = new List<string>(argv.Count + 1);
        for (var i = 0; i < argv.Count; i++)
        {
            withProgress.Add(argv[i]);
            if (i == anchor)
            {
                withProgress.Add("--progress");
            }
        }

        return withProgress;
    }

    /// <summary>
    /// Drains one stream to completion, stamping <paramref name="activity"/> on every line so the watchdog
    /// can tell "working" from "stuck", and reporting throttled progress when this is a remote operation's
    /// stderr (where git writes it). Reading line-by-line rather than to the end is what makes both
    /// possible: <c>ReadToEndAsync</c> yields nothing until the process is over, which is precisely when
    /// the information stops being worth having.
    /// </summary>
    private async Task<string> ReadStreamAsync(
        StreamReader reader,
        ActivityClock activity,
        string? progressVerb,
        CancellationToken cancellationToken
    )
    {
        var all = new System.Text.StringBuilder();
        var lastReport = Stopwatch.StartNew();
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                activity.Touch();

                // A progress line counts as activity and may be reported, but is never accumulated. It is
                // the only unbounded source here — a 969,911-object fetch emits thousands, each its own
                // line because git separates progress with '\r' — and this buffer becomes the exception
                // message a failure gets diagnosed from. Keeping them would bury a real
                // "fatal: could not read from remote" in a wall of percentages, trading one illegible
                // failure for another, and would grow the buffer without limit while doing it.
                var isProgress = progressVerb is not null && IsProgressLine(line);
                if (!isProgress)
                {
                    _ = all.Append(line).Append('\n');
                }

                if (
                    progressVerb is not null
                    && lastReport.Elapsed >= S_progressInterval
                    && !string.IsNullOrWhiteSpace(line)
                )
                {
                    lastReport.Restart();
                    _logger.LogInformation("Host git {Verb} still working: {Progress}", progressVerb, line.Trim());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Killed, or shutting down — whatever was captured before that is still worth returning.
        }
        catch (ObjectDisposedException)
        {
            // The process was killed out from under the reader; same treatment.
        }

        return all.ToString();
    }

    /// <summary>
    /// Cancels <paramref name="timeoutCts"/> once the command has been silent for longer than the idle
    /// timeout, or running for longer than the ceiling.
    /// </summary>
    private async Task WatchdogAsync(
        ActivityClock activity,
        Stopwatch started,
        TimeSpan idleTimeout,
        CancellationTokenSource timeoutCts,
        CancellationToken cancellationToken
    )
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(S_watchdogTick, cancellationToken).ConfigureAwait(false);

                if (started.Elapsed > _maxDuration)
                {
                    activity.TimedOutOnCeiling = true;
                    await timeoutCts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                if (activity.SinceLastOutput > idleTimeout)
                {
                    await timeoutCts.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The command finished first, which is the normal way out of this loop.
        }
    }

    /// <summary>
    /// Kills the process and everything it spawned. Without this a cancelled wait simply abandons the
    /// child: a shutdown mid-fetch left a 969,911-object fetch running detached, still holding the store.
    /// Rare while only shutdown could cancel — routine the moment a timeout can.
    /// </summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, or gone between the check and the kill. Either way there is nothing left to
            // kill and nothing a caller could do about it.
        }
    }

    /// <summary>
    /// Waits for a killed tree to stop holding the packs it was writing, bounded by
    /// <see cref="S_reapDeadline"/>. Returns whether it settled, and how long that took.
    /// </summary>
    /// <remarks>
    /// Two conditions, and the second is the one that matters. <see cref="Process.HasExited"/> covers our own
    /// direct child, which is all <see cref="Process"/> can tell us about — but the process holding the
    /// descriptor is <c>index-pack</c>, a GRANDCHILD, which on Linux is reparented to init when git dies and
    /// reaped there rather than by us. So the tree's exit is observed through the descriptors themselves,
    /// which is both the honest signal and precisely the one the sweep is about to consult.
    /// <para>
    /// Never throws and never waits unbounded: a cleanup path that can hang is worse than the leak it is
    /// cleaning up after, and this one runs on every shutdown.
    /// </para>
    /// </remarks>
    private static async Task<(bool Settled, TimeSpan Waited)> WaitForKilledTreeAsync(
        Process process,
        string? workingDirectory,
        IReadOnlySet<string> preexisting
    )
    {
        var waited = Stopwatch.StartNew();

        while (true)
        {
            if (HasExited(process) && !OrphanedPackSweeper.AnyNewTempPackStillOpen(workingDirectory, preexisting))
            {
                return (true, waited.Elapsed);
            }

            if (waited.Elapsed >= S_reapDeadline)
            {
                return (false, waited.Elapsed);
            }

            await Task.Delay(S_reapPollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the child has been reaped. Any failure answers YES, so a process we can no longer ask about
    /// cannot hold the reap wait at its deadline.
    /// </summary>
    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Removes the temp packs this command abandoned, and says how much that reclaimed. Reported at Warning
    /// rather than Debug because it is the audible half of the fix: a sweep that runs and says nothing is
    /// indistinguishable from a sweep that never runs, which is exactly how the original leak stayed
    /// invisible until it filled the disk.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT gated behind <c>CodeReviewDaemonOptions.EnableObjectStoreMaintenance</c>, and that
    /// is the opposite of what it looks like. That flag governs the <c>repack</c> that
    /// <see cref="Git.MergeBaseResolver"/> runs between deepening rounds — a command that rewrites an object
    /// store IN PLACE, which the owner's standing instruction puts off limits. It defaults to false and is
    /// set in no appsettings file, so it is false everywhere the daemon actually runs. Gating this behind it
    /// would therefore not make the sweep cautious, it would make it DEAD, and the leak it exists to stop is
    /// the one that already took the filesystem read-only.
    /// <para>
    /// The grant is narrower than the flag and this stays inside it: the only files removed are temp files
    /// OUR OWN killed command created and abandoned within this method's own window. No real pack can be
    /// reached — not by an unusual name, not by a race — because the prefix rule and the pre-command
    /// snapshot are both required, and neither is configurable.
    /// </para>
    /// </remarks>
    private void SweepAbandonedPacks(
        string? workingDirectory,
        IReadOnlySet<string>? preexisting,
        string verb,
        bool settled,
        TimeSpan waited
    )
    {
        if (preexisting is null)
        {
            return;
        }

        var (files, bytes) = OrphanedPackSweeper.SweepNew(workingDirectory, preexisting, _logger);
        if (files > 0)
        {
            _logger.LogWarning(
                "Killed git {Verb} in '{WorkingDirectory}' had abandoned {Files} temporary pack file(s); "
                    + "removed them, reclaiming {ReclaimedMegabytes:0.#} MB "
                    + "(tree released them after {WaitSeconds:0.##}s).",
                verb,
                workingDirectory,
                files,
                bytes / (1024d * 1024d),
                waited.TotalSeconds
            );
            return;
        }

        // Zero swept is TWO OUTCOMES that must not look alike, and conflating them is how this defect hid.
        // "Nothing was abandoned" is the ordinary case and rightly says nothing. "Something was abandoned
        // and could not be taken" is a live leak of gigabytes, and it used to return down the same silent
        // path — so the failure mode was shaped exactly like success. Only the second is reported.
        var stillHeld = OrphanedPackSweeper.AnyNewTempPackStillOpen(workingDirectory, preexisting);
        if (!stillHeld && settled)
        {
            return;
        }

        _logger.LogWarning(
            "Killed git {Verb} in '{WorkingDirectory}' swept NOTHING (still held open by a live process: "
                + "{StillHeld}; killed tree settled within the deadline: {TreeSettled}, after "
                + "{WaitSeconds:0.##}s). Any temp pack it abandoned is still on disk, and nothing reclaims "
                + "one inside a useful window — git only prunes temp files past gc.pruneExpire, which "
                + "defaults to two weeks.",
            verb,
            workingDirectory,
            stillHeld,
            settled,
            waited.TotalSeconds
        );
    }

    /// <summary>
    /// Waits for both readers to reach EOF, bounded by the drain deadline. Returns true only when both
    /// drained naturally, which is the sole condition under which the captured output is known to be the
    /// whole of what the command produced.
    /// </summary>
    /// <remarks>
    /// On the deadline it cancels <paramref name="timeoutCts"/> — the token the readers are reading on —
    /// so their partial content becomes collectable instead of pending forever. That is the same
    /// cancellation whose misplacement caused this defect; here it is deliberate and its consequence is
    /// reported, which is the entire difference.
    /// </remarks>
    private static async Task<bool> DrainWithinDeadlineAsync(
        Task<string> stdout,
        Task<string> stderr,
        CancellationTokenSource timeoutCts
    )
    {
        var drain = Task.WhenAll(stdout, stderr);
        using var deadline = new CancellationTokenSource(S_drainDeadline);
        try
        {
            _ = await drain.WaitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            await timeoutCts.CancelAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception)
        {
            // A reader faulted outright (an IOException on the pipe, say). SafeResultAsync would turn that
            // into an empty string — the same silent truncation this exists to remove — so it counts as an
            // incomplete capture rather than as an empty answer.
            return false;
        }
    }

    /// <summary>Awaits a stream-drain that may have been cancelled, yielding whatever it captured.</summary>
    private static async Task<string> SafeResultAsync(Task<string> read)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The watchdog only ever ends by cancellation; nothing it could report is actionable.
        }
    }

    /// <summary>
    /// The git verb in an argv that begins with <c>git</c> and any number of <c>-c &lt;config&gt;</c> /
    /// <c>-C &lt;dir&gt;</c> pairs (which <see cref="Git.GitRunner"/> always prepends), or null when there
    /// is no verb to find.
    /// </summary>
    private static string? GitVerb(IReadOnlyList<string> argv)
    {
        for (var i = 1; i < argv.Count; i++)
        {
            if (argv[i] is "-c" or "-C")
            {
                i++; // skip the pair's value
                continue;
            }

            if (!argv[i].StartsWith('-'))
            {
                return argv[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Index of the token AFTER which <c>--progress</c> must be inserted, or -1 when it must not be added
    /// at all.
    /// <para>
    /// For every verb but one this is the verb itself. <c>submodule</c> is the exception, and getting it
    /// wrong is not a cosmetic error: <c>git submodule --progress update --init</c> is REJECTED with a usage
    /// dump (verified on git 2.53.0), so inserting at the verb would have broken every submodule command the
    /// daemon runs while looking like a pure logging improvement. The flag belongs to the subcommand.
    /// </para>
    /// <para>
    /// Only <c>update</c> qualifies, because it is the only submodule subcommand that fetches. <c>sync</c>,
    /// <c>status</c> and the rest neither talk to a remote nor accept the flag.
    /// </para>
    /// </summary>
    private static int ProgressAnchorIndex(IReadOnlyList<string> argv, string verb)
    {
        var verbIndex = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], verb, StringComparison.Ordinal))
            {
                verbIndex = i;
                break;
            }
        }

        if (verbIndex < 0)
        {
            return -1;
        }

        if (!string.Equals(verb, "submodule", StringComparison.Ordinal))
        {
            return verbIndex;
        }

        // Skip the options git allows BEFORE the subcommand (--quiet, --cached) to find the subcommand.
        for (var i = verbIndex + 1; i < argv.Count; i++)
        {
            if (argv[i].StartsWith('-'))
            {
                continue;
            }

            return string.Equals(argv[i], "update", StringComparison.Ordinal) ? i : -1;
        }

        return -1;
    }

    /// <summary>Last-output timestamp, written by the stream readers and read by the watchdog.</summary>
    private sealed class ActivityClock
    {
        private long _lastTicks = Stopwatch.GetTimestamp();

        /// <summary>Set by the watchdog so the timeout message can name which bound was hit.</summary>
        public bool TimedOutOnCeiling { get; set; }

        public TimeSpan SinceLastOutput => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastTicks));

        public void Touch() => Interlocked.Exchange(ref _lastTicks, Stopwatch.GetTimestamp());
    }
}
