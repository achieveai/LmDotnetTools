using AchieveAi.LmDotnetTools.LmTestUtils;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Issue #479 item 3 — <see cref="RealGitFixture.ForceDelete"/> runs from <c>Dispose</c>, and an exception
/// leaving a <c>using</c>'s Dispose REPLACES the one already in flight. A fixture whose temp tree could not
/// be deleted would therefore erase the assertion failure the test was reporting and substitute a complaint
/// about TEMP — the exact masking class the daemon's own teardown paths are guarded against. Cleaning up is
/// never worth more than the result it would hide.
/// </summary>
public sealed class RealGitFixtureTests
{
    [WindowsOnlyFact(
        "an open file handle only blocks deletion on Windows; on Unix the unlink succeeds and there would be "
            + "nothing for the final attempt to swallow, so the body would pass while proving nothing"
    )]
    public void ForceDelete_does_not_throw_out_of_dispose_when_the_tree_cannot_be_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "crd-forcedelete-" + Guid.NewGuid().ToString("N"));
        var pinned = Path.Combine(root, "pinned.bin");
        _ = Directory.CreateDirectory(root);
        File.WriteAllText(pinned, "x");

        using (var _ = new FileStream(pinned, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => RealGitFixture.ForceDelete(root);

            act.Should()
                .NotThrow("a throw here would replace whatever failure the test being disposed was actually reporting");

            // Non-vacuity: the delete has to have genuinely failed, or "did not throw" is the trivial truth
            // about a tree that simply went away.
            Directory
                .Exists(root)
                .Should()
                .BeTrue("the handle must really have blocked the delete for the swallow to be what was exercised");
        }

        // The handle is released; the same call now cleans up for real, so the assertion above is about the
        // final attempt swallowing — not about ForceDelete having stopped deleting anything.
        RealGitFixture.ForceDelete(root);
        Directory.Exists(root).Should().BeFalse("an unblocked tree is still deleted");
    }
}
