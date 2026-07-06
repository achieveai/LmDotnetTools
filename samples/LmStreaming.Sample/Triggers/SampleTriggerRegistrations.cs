using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Assembles the sample host's <see cref="TriggerOptions"/> — the built-in <c>timer</c> kind plus
/// the sample-app sources (file_tail, schedule, subagent always; process only when Sandbox is on),
/// registered through <see cref="TriggerOptions.AdditionalRegistrations"/>.
/// </summary>
public static class SampleTriggerRegistrations
{
    public static TriggerOptions Build(bool sandboxEnabled)
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
            Source = new FileTailTriggerSource(fileTailRoots),
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

        // (#144) subagent registration appended here in a later task.
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
