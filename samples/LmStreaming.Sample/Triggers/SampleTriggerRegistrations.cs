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

        // (#141) file_tail, (#143) schedule, (#144) subagent registrations appended here in later tasks.
        // (#142) process registration appended here, guarded by `if (sandboxEnabled)`, in Task 9.

        return new TriggerOptions
        {
            AdditionalRegistrations = registrations,
        };
    }
}
