using System.Runtime.InteropServices;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped off Windows, with the reason stated in the run.
/// <para>
/// For an arrangement that only holds on Windows, running the same body elsewhere is worse than not running
/// it: the setup "succeeds", the assertions fail (or a green-but-vacuous pass reports nothing) for a reason
/// that has nothing to do with the defect the test targets. Skipping says so out loud instead, in the one
/// place anyone reads — the test run itself. The .NET suite's CI job is <c>windows-latest</c>, so a
/// Windows-gated fact still runs on every PR.
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
