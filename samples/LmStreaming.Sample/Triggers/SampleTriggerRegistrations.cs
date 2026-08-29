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
                    Description =
                        "Fire when a backgrounded sandbox Bash process exits with a matching exit code / stdout. "
                        + "Start the work yourself via the Bash tool using the wait-file convention, then arm with the handle you chose: "
                        + "mkdir -p .lm-waits/<handle> && { cmd > .lm-waits/<handle>/out 2>&1; echo $? > .lm-waits/<handle>/exit; } & "
                        + "(handle: letters/digits/._- only, max 64, no leading dot).",
                    ArgsSchema = ProcessTriggerSource.ArgsSchemaText,
                    Capabilities = ProcessTriggerSource.Capabilities,
                    Source = new ProcessTriggerSource(processExitObserver ?? NoopProcessExitObserver.Instance),
                }
            );
        }

        return new TriggerOptions { AdditionalRegistrations = registrations };
    }
}
