using FluentAssertions;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Teardown must not fail a test that already passed.
/// </summary>
/// <remarks>
/// Seen on CI: <c>Deep_link_into_in_flight_daemon_run_resumes_sub_agent_live</c> reported as FAILED
/// with <c>IOException: Directory not empty</c> thrown from <c>Dispose</c> — every assertion in the
/// test body had succeeded. The host stop is bounded at 5 seconds and a stop that exceeds it is
/// swallowed, so a daemon run can still be appending under <c>conversations/</c> when the recursive
/// delete runs. That delete is not atomic: it enumerates, unlinks what it saw, then removes the
/// parent, and a file created between those steps makes the last step throw.
/// </remarks>
public sealed class BrowserWebAppFactoryTempCleanupTests
{
    [Fact]
    public void A_delete_that_succeeds_first_time_is_not_retried()
    {
        var calls = 0;

        BrowserWebAppFactory.TryDeleteFactoryTempDirectory("/tmp/whatever", _ => calls++);

        calls.Should().Be(1, "the happy path must not pay for the retry loop");
    }

    [Fact]
    public void A_transient_failure_is_retried_until_the_writer_finishes()
    {
        // The real shape: the racing writer is finishing, not restarting, so the delete that failed
        // a moment ago succeeds shortly after.
        var calls = 0;

        BrowserWebAppFactory.TryDeleteFactoryTempDirectory(
            "/tmp/whatever",
            _ =>
            {
                calls++;
                if (calls < 3)
                {
                    throw new IOException("Directory not empty : '/tmp/whatever/conversations'");
                }
            }
        );

        calls.Should().Be(3);
    }

    [Fact]
    public void A_delete_that_never_succeeds_does_not_fail_the_test_that_already_passed()
    {
        // The assertion that matters. This runs in Dispose, after the test body's assertions have
        // already succeeded, so an escaping exception turns a green test red for a reason unrelated
        // to anything it asserted — which is exactly the CI failure this fixes.
        var calls = 0;

        var act = () =>
            BrowserWebAppFactory.TryDeleteFactoryTempDirectory(
                "/tmp/whatever",
                _ =>
                {
                    calls++;
                    throw new IOException("Directory not empty : '/tmp/whatever/conversations'");
                }
            );

        act.Should().NotThrow();
        calls.Should().Be(5, "the attempt budget is bounded — teardown cannot retry forever");
    }

    [Fact]
    public void An_already_removed_directory_is_the_desired_end_state_not_an_error()
    {
        var act = () =>
            BrowserWebAppFactory.TryDeleteFactoryTempDirectory(
                "/tmp/whatever",
                _ => throw new DirectoryNotFoundException("gone")
            );

        act.Should().NotThrow();
    }

    /// <summary>
    /// Only the two filesystem-contention exceptions are tolerated. A bug in the cleanup path itself
    /// must still surface — swallowing everything would hide it forever, since nothing else looks at
    /// this directory afterwards.
    /// </summary>
    [Fact]
    public void An_unexpected_exception_is_not_swallowed()
    {
        var act = () =>
            BrowserWebAppFactory.TryDeleteFactoryTempDirectory(
                "/tmp/whatever",
                _ => throw new InvalidOperationException("a real bug in the cleanup path")
            );

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_default_delete_removes_a_real_directory_tree()
    {
        // Pins the production default actually wired up behind the seam: every test above substitutes
        // the delete, so without this row they would all pass against a default that deletes nothing.
        var root = Path.Combine(Path.GetTempPath(), $"lm-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        File.WriteAllText(Path.Combine(root, "conversations", "a.jsonl"), "{}");

        BrowserWebAppFactory.TryDeleteFactoryTempDirectory(root);

        Directory.Exists(root).Should().BeFalse();
    }
}
