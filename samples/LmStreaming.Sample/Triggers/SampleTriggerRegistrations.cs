using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Assembles the sample host's <see cref="TriggerOptions"/> — the built-in <c>timer</c> kind plus
/// the sample-app sources (file_tail, schedule, subagent always; process only when Sandbox is on),
/// registered through <see cref="TriggerOptions.AdditionalRegistrations"/>.
/// </summary>
public static class SampleTriggerRegistrations
{
    /// <param name="sandboxEnabled">Whether the sandbox session is active (gates the process kind).</param>
    /// <param name="subAgentManagerAccessor">
    /// Lazily resolves the loop's <see cref="SubAgentManager"/> (the loop builds it inside its own
    /// ctor, after these registrations are assembled, so it can't be handed in directly). Null skips
    /// registering the <c>subagent</c> kind entirely; a non-null accessor may itself still resolve to
    /// null at arm time (e.g. a conversation with no sub-agent orchestration configured), which the
    /// source rejects as an arm-time <see cref="ArgumentException"/>.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional. Supplies the <c>file_tail</c> watcher's logger; without one a poll loop that has
    /// gone structurally blind (its file deleted, its volume unmounted, an ACL change) cannot say
    /// so, and the wait's TTL expiry reads as "nothing matched" rather than "nothing could be
    /// observed" (#161).
    /// </param>
    /// <param name="processExitObserver">
    /// Optional real Bash-tool exit bridge for the <c>process</c> kind (issue #142) — in production the
    /// sandbox-session-scoped <see cref="SandboxProcessExitObserver"/>. Null keeps the
    /// <see cref="NoopProcessExitObserver"/> placeholder, whose arm-time rejection tells the model the
    /// kind is not wired rather than parking a wait until TTL.
    /// </param>
    public static TriggerOptions Build(
        bool sandboxEnabled,
        Func<SubAgentManager?>? subAgentManagerAccessor = null,
        ILoggerFactory? loggerFactory = null,
        IProcessExitObserver? processExitObserver = null
    )
    {
        var registrations = new List<TriggerSourceRegistration>();

        // (#141) file_tail: unconditional — tails a file under a host-fixed allowed root regardless
        // of sandbox availability.
        var fileTailRoots = new[] { Path.Combine(Path.GetTempPath(), "lmstreaming-tails") };
        registrations.Add(
            new TriggerSourceRegistration
            {
                Kind = FileTailTriggerSource.KindName,
                Description = "Fire when a matching line is appended to an allowed log file.",
                ArgsSchema = FileTailTriggerSource.ArgsSchemaText,
                Capabilities = FileTailTriggerSource.Capabilities,
                // Redacted (the default) rather than MetadataOnly: a sample host tailing its own temp
                // directory wants the matched line to stay useful. A deployment tailing files that can
                // carry customer data should pass MetadataOnly instead — pattern redaction removes the
                // shapes it knows and makes no promise about the rest.
                Source = new FileTailTriggerSource(
                    fileTailRoots,
                    FileTailContentMode.Redacted,
                    loggerFactory?.CreateLogger<FileTailTriggerSource>()
                ),
            }
        );

        // (#143) schedule: unconditional — fires on a cron expression or a fixed interval.
        registrations.Add(
            new TriggerSourceRegistration
            {
                Kind = ScheduleTriggerSource.KindName,
                Description = "Fire on a cron expression or a fixed interval (block resolves once; notify repeats).",
                ArgsSchema = ScheduleTriggerSource.ArgsSchemaText,
                Capabilities = ScheduleTriggerSource.Capabilities,
                Source = new ScheduleTriggerSource(),
            }
        );

        // (#144) subagent: registered only when the conversation has sub-agent orchestration
        // configured — the source needs a live SubAgentManager to observe.
        if (subAgentManagerAccessor != null)
        {
            registrations.Add(
                new TriggerSourceRegistration
                {
                    Kind = SubAgentCompletionTriggerSource.KindName,
                    Description = "Fire when a specific spawned sub-agent completes.",
                    ArgsSchema = SubAgentCompletionTriggerSource.ArgsSchemaText,
                    Capabilities = SubAgentCompletionTriggerSource.Capabilities,
                    Source = new SubAgentCompletionTriggerSource(subAgentManagerAccessor),
                }
            );
        }

        // (#142) process: sandbox-gated. With a real observer supplied the kind actually fires; the
        // Noop fallback keeps the arm-time "not wired in this host" rejection.
        if (sandboxEnabled)
        {
            registrations.Add(
                new TriggerSourceRegistration
                {
                    Kind = ProcessTriggerSource.KindName,
                    // The observer always reads .lm-waits/... relative to the WORKSPACE ROOT, while
                    // the taught mkdir is relative to the Bash tool's current directory — the two
                    // coincide only at the tool's starting directory (workingDirectoryOverride pins
                    // it to the workspace root). The description says so explicitly, because a
                    // relative mkdir run after a cd into a subdirectory drops the files somewhere
                    // the observer never looks and the wait silently parks to TTL (#598 review
                    // F-005). No `~` anchor: the local backend has no '/workspace' mount and the
                    // tool's home is not the workspace root there. Keep the shell foreground within
                    // the Gateway-managed background Bash call so its exec remains tracked.
                    Description =
                        "Fire when a sandbox Bash command exits with a matching exit code / stdout. "
                        + "From the workspace root, start a Gateway Bash tool call with run_in_background=true "
                        + "and command: mkdir -p .lm-waits/<handle> && { cmd > .lm-waits/<handle>/out 2>&1; "
                        + "rc=$?; printf '%s\\n' \"$rc\" > .lm-waits/<handle>/exit; }. "
                        + "Do not append '&' or daemonize cmd: the shell must stay alive until cmd exits. "
                        + "Use timeout=0 for unbounded work, or a suitable finite timeout. The .lm-waits directory "
                        + "MUST be directly under the workspace root; if you changed directory, use an absolute "
                        + "workspace path. The Gateway task ID is not the wait-file handle. Arm Wait with the "
                        + "fresh handle you chose (letters/digits/._- only, max 64, no leading dot).",
                    ArgsSchema = ProcessTriggerSource.ArgsSchemaText,
                    Capabilities = ProcessTriggerSource.Capabilities,
                    Source = new ProcessTriggerSource(processExitObserver ?? NoopProcessExitObserver.Instance),
                }
            );
        }

        return new TriggerOptions { AdditionalRegistrations = registrations };
    }
}
