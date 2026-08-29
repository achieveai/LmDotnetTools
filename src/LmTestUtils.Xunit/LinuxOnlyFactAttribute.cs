using System.Runtime.InteropServices;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped off Linux, with the reason stated in the run.
/// <para>
/// The counterpart to <see cref="WindowsOnlyFactAttribute"/>, and it exists for the same reason: a test whose
/// arrangement only holds on one kernel must SAY it did not run rather than pass without executing anything.
/// The alternative in the wild is <c>if (!OperatingSystem.IsLinux()) return;</c> at the top of the body,
/// which is strictly worse — a body that returns early is reported as a PASS, and a green run then carries a
/// claim nobody checked. A skipped test is the one signal a reader can act on.
/// </para>
/// <para>
/// Read the asymmetry honestly: this repository's .NET CI job is <c>windows-latest</c>, so a Windows-gated
/// fact runs on every PR while a Linux-gated one runs only where a Linux leg exists. Gate a test this way
/// only when the platform is genuinely load-bearing — <c>/proc</c>, POSIX signals, mode bits — never as a
/// way to quiet a test that merely happens to fail elsewhere.
/// </para>
/// <para>
/// xunit 2 has no <c>Assert.Skip</c>, and <c>Skip</c> on the attribute is a compile-time constant, so the
/// decision is made here in the constructor where the platform is knowable.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Skip = $"Linux-only: {because}";
        }
    }
}
