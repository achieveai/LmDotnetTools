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
    public static TriggerOptions Build(
        bool sandboxEnabled,
        Func<SubAgentManager?>? subAgentManagerAccessor = null,
        ILoggerFactory? loggerFactory = null)
    {
        var registrations = new List<TriggerSourceRegistration>();

        // (#141) file_tail: unconditional — tails a file under a host-fixed allowed root regardless
        // of sandbox availability.
        var fileTailRoots = new[] { Path.Combine(Path.GetTempPath(), "lmstreaming-tails") };
        registrations.Add(new TriggerSourceRegistration
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
                loggerFactory?.CreateLogger<FileTailTriggerSource>()),
        });

        // (#143) schedule: unconditional — fires on a cron expression or a fixed interval.
        registrations.Add(new TriggerSourceRegistration
        {
            Kind = ScheduleTriggerSource.KindName,
            Description = "Fire on a cron expression or a fixed interval (block resolves once; notify repeats).",
            ArgsSchema = ScheduleTriggerSource.ArgsSchemaText,
            Capabilities = ScheduleTriggerSource.Capabilities,
            Source = new ScheduleTriggerSource(),
        });

        // (#144) subagent: registered only when the conversation has sub-agent orchestration
        // configured — the source needs a live SubAgentManager to observe.
        if (subAgentManagerAccessor != null)
        {
            registrations.Add(new TriggerSourceRegistration
            {
                Kind = SubAgentCompletionTriggerSource.KindName,
                Description = "Fire when a specific spawned sub-agent completes.",
                ArgsSchema = SubAgentCompletionTriggerSource.ArgsSchemaText,
                Capabilities = SubAgentCompletionTriggerSource.Capabilities,
                Source = new SubAgentCompletionTriggerSource(subAgentManagerAccessor),
            });
        }

        // (#142) process registration appended here, guarded by `if (sandboxEnabled)`, in Task 9.
        if (sandboxEnabled)
        {
            registrations.Add(new TriggerSourceRegistration
            {
                Kind = ProcessTriggerSource.KindName,
                Description = "Fire when a sandbox process exits with a matching exit code / stdout.",
                ArgsSchema = ProcessTriggerSource.ArgsSchemaText,
                Capabilities = ProcessTriggerSource.Capabilities,
                // Placeholder observer: wire a real IProcessExitObserver over the Bash-tool process
                // registry to make this kind actually fire in production (documented follow-up).
                Source = new ProcessTriggerSource(NoopProcessExitObserver.Instance),
            });
        }

        return new TriggerOptions
        {
            AdditionalRegistrations = registrations,
        };
    }
}
