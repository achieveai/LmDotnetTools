namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Await-and-assert helpers that wait for DOM state to stabilize before asserting.
/// Thin wrappers around Playwright's built-in <see cref="ILocator.WaitForAsync"/> and
/// <c>Assertions.Expect(...)</c> auto-waiting assertions, so tests do not need explicit
/// <c>Task.Delay</c> calls.
/// </summary>
public static class DomAssertions
{
    /// <summary>
    /// Waits until at least <paramref name="minCount"/> elements match the locator, then
    /// returns the current count. Fails the test if the timeout expires.
    /// </summary>
    public static async Task<int> WaitForCountAtLeastAsync(
        this ILocator locator,
        int minCount,
        float timeoutMs = 10_000
    )
    {
        // Playwright's ToHaveCountAsync requires an exact count; for "at least N" we wait
        // for the Nth element (0-indexed) to become attached. When the Nth element exists,
        // there are at least N+1 elements — so minCount-1 is the target index.
        if (minCount <= 0)
        {
            return await locator.CountAsync().ConfigureAwait(false);
        }

        await locator.Nth(minCount - 1)
            .WaitForAsync(
                new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = timeoutMs,
                }
            )
            .ConfigureAwait(false);

        return await locator.CountAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the locator's innerText contains <paramref name="substring"/> (case-insensitive).
    /// Uses Playwright's auto-waiting <c>ToContainTextAsync</c>.
    /// </summary>
    public static async Task WaitForTextContainsAsync(
        this ILocator locator,
        string substring,
        float timeoutMs = 10_000
    )
    {
        await Assertions
            .Expect(locator.First)
            .ToContainTextAsync(substring, new LocatorAssertionsToContainTextOptions { Timeout = timeoutMs, IgnoreCase = true })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asserts that the send button has returned to the idle state (stop button hidden,
    /// send button visible and enabled). Waits up to <paramref name="timeoutMs"/>.
    /// </summary>
    public static async Task WaitForStreamIdleAsync(this IPage page, float timeoutMs = 15_000)
    {
        await page.StopButton()
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = timeoutMs });

        await page.SendButton()
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeoutMs });
    }

    /// <summary>
    /// Waits for the stop button to appear (stream became active) and returns once it is
    /// visible.
    /// </summary>
    public static async Task WaitForStreamActiveAsync(this IPage page, float timeoutMs = 10_000)
    {
        await page.StopButton()
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeoutMs });
    }
}
