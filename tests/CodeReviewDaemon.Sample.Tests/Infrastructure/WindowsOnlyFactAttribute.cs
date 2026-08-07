using System.Runtime.InteropServices;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped off Windows, with the reason stated in the run.
/// <para>
/// For a regression that only exists on Windows, running the same body elsewhere is worse than not running it:
/// the setup succeeds, the assertion holds for a reason that has nothing to do with the defect, and the test
/// reports green while proving nothing. A green-but-vacuous test is what lets the fix be reverted unnoticed.
/// Skipping says so out loud instead, in the one place anyone reads — the test run itself.
/// </para>
/// <para>
/// xunit 2 has no <c>Assert.Skip</c>, and <c>Skip</c> on the attribute is a compile-time constant, so the
/// decision is made here in the constructor where the platform is knowable.
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
