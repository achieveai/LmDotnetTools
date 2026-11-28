namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// A real-time bound on a step that <see cref="ScriptedClock"/> does not drive: a signal from the fake gateway, or the
/// call under test finishing. Product timing stays on the scripted clock; this only turns a regression that would hang
/// the run into a failure that names the stage. On a hang it cancels the outstanding call, so nothing is left running.
/// </summary>
internal static class Watchdog
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    public static async Task WithinAsync(this Task step, string stage, CancellationTokenSource outstanding)
    {
        try
        {
            await step.WaitAsync(Limit);
        }
        catch (TimeoutException ex) when (!step.IsCompleted)
        {
            await outstanding.CancelAsync();
            throw new TimeoutException($"No progress within {Limit.TotalSeconds:0} s at: {stage}.", ex);
        }
    }

    public static async Task<T> WithinAsync<T>(this Task<T> step, string stage, CancellationTokenSource outstanding)
    {
        await ((Task)step).WithinAsync(stage, outstanding);
        return await step;
    }
}
