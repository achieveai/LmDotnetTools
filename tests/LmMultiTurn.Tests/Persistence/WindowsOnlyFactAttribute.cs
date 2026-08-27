using System.Runtime.InteropServices;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped off Windows, with the reason stated in the run.
/// <para>
/// For an arrangement that only holds on Windows, running the same body elsewhere is worse than not running
/// it: the setup "succeeds", the assertions fail for a reason that has nothing to do with the defect, and the
/// suite goes red on a platform where the defect does not exist. Skipping says so out loud instead, in the one
/// place anyone reads — the test run itself. The .NET suite's CI job is <c>windows-latest</c>, so a
/// Windows-gated fact still runs on every PR.
/// </para>
/// <para>
/// xunit 2 has no <c>Assert.Skip</c>, and <c>Skip</c> on the attribute is a compile-time constant, so the
/// decision is made here in the constructor where the platform is knowable.
/// </para>
/// <para>
/// This duplicates <c>CodeReviewDaemon.Sample.Tests.Infrastructure.WindowsOnlyFactAttribute</c>, which is
/// <c>public</c> but sits in a test assembly this one does not reference. The duplication is deliberate
/// scope control, not a missing shared home: <c>src/LmTestUtils</c> is referenced by both and could hold
/// one copy, but it is a shipped library, so hoisting an xunit attribute into its public surface is a
/// separate decision from this PR. Deduplicating the two belongs with the same follow-up that moves
/// <see cref="DetachedStoreTeardown"/>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = $"Windows-only: {because}";
        }
    }
}
