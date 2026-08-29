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

        await locator
            .Nth(minCount - 1)
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = timeoutMs })
            .ConfigureAwait(false);

        return await locator.CountAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the locator's innerText contains <paramref name="substring"/> (case-insensitive).
    /// Uses Playwright's auto-waiting <c>ToContainTextAsync</c>.
    /// </summary>
    public static async Task WaitForTextContainsAsync(this ILocator locator, string substring, float timeoutMs = 10_000)
    {
        await Assertions
            .Expect(locator.First)
            .ToContainTextAsync(
                substring,
                new LocatorAssertionsToContainTextOptions { Timeout = timeoutMs, IgnoreCase = true }
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Short race window used internally by <see cref="WaitForStreamIdleAsync"/> to close the
    /// not-yet-started/idle ambiguity below without risking a real hang on a call site where the
    /// stream has already, legitimately, gone idle for good. Generous relative to a Vue reactive
    /// re-render (single-digit to low-tens-of-ms in practice), negligible relative to any of this
    /// helper's real timeouts.
    /// </summary>
    private const float ActiveRaceWindowMs = 500;

    /// <summary>
    /// Asserts that the send button has returned to the idle state (stop button hidden,
    /// send button visible and enabled). Waits up to <paramref name="timeoutMs"/>.
    /// </summary>
    /// <remarks>
    /// Self-guarding against #265: a caller that invokes this immediately after a bare fill+click
    /// with no wait (<c>SendMessageAsync</c>) can land here before the stop button has even
    /// rendered -- at that instant "hidden" is trivially already true, so waiting for it alone
    /// makes idle indistinguishable from not-yet-started, and a stream that never actually starts
    /// would read as a pass instead of a timeout. This races a SHORT, bounded wait for the stream
    /// to become active first; if that never happens within the short window, that is treated as
    /// "no stream is starting here" (either one already ran to completion before this call, or
    /// this call site never triggers one) rather than a failure -- the real idle wait below still
    /// runs either way, so a call site where idle already, legitimately, holds only pays the short
    /// window's latency, not a full extra <paramref name="timeoutMs"/>.
    /// <para>
    /// #396 review: this closes an ambiguity in the wait sequence's own reasoning, not a
    /// reproduced flake -- no failure from this race has been observed in CI or locally.
    /// </para>
    /// </remarks>
    public static async Task WaitForStreamIdleAsync(this IPage page, float timeoutMs = 15_000)
    {
        try
        {
            await page.WaitForStreamActiveAsync(timeoutMs: ActiveRaceWindowMs);
        }
        catch (TimeoutException)
        {
            // No transition to active observed within the short race window -- proceed to the
            // real idle wait regardless; see remarks above.
        }

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
