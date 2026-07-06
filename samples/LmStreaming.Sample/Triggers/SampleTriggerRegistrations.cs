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

        // (#143) schedule, (#144) subagent registrations appended here in later tasks.
        // (#142) process registration appended here, guarded by `if (sandboxEnabled)`, in Task 9.

        return new TriggerOptions
        {
            AdditionalRegistrations = registrations,
        };
    }
}
