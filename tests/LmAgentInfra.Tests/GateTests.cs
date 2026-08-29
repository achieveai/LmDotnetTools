namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests;

/// <summary>
/// Covers the wait ceiling itself. Every other suite in this assembly reaches <see cref="Gate"/>
/// through <c>WaitForAttemptsAsync</c>/<c>WaitForResolutionsAsync</c>/<c>WaitForTimerAsync</c>, and
/// all of them assume a condition that eventually holds — so nothing else exercises what happens
/// when one does not. That case is not hypothetical: it wedged the testhost on CI until the
/// inactivity blame-dump aborted the whole solution run.
/// </summary>
public class GateTests
{
    [Fact]
    public async Task A_condition_that_never_holds_fails_the_test_instead_of_wedging_it()
    {
        var gate = new Gate(TimeSpan.FromMilliseconds(50));

        var wait = async () => await gate.WaitAsync(() => false);

        (await wait.Should().ThrowAsync<TimeoutException>()).WithMessage(
            "*A_condition_that_never_holds_fails_the_test_instead_of_wedging_it*",
            "the message must name the waiter, because the whole point is telling which wait "
                + "stalled without a hang dump"
        );
    }

    [Fact]
    public async Task A_condition_satisfied_by_a_later_signal_still_completes()
    {
        // The ceiling must not turn an edge-triggered wait into a poll: this condition is false when
        // the waiter first checks it, and only a Signal wakes it.
        var gate = new Gate(TimeSpan.FromSeconds(30));
        var held = false;

        var wait = gate.WaitAsync(() => Volatile.Read(ref held));
        wait.IsCompleted.Should().BeFalse("the condition does not hold yet");

        Volatile.Write(ref held, true);
        gate.Signal();

        await wait.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
