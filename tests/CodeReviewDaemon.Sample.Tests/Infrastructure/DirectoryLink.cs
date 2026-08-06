using System.Diagnostics;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>Builds the redirected directory the daemon's host-side tree walks must refuse to cross.</summary>
internal static class DirectoryLink
{
    /// <summary>
    /// Points <paramref name="link"/> at <paramref name="target"/>, then asserts the result really is a reparse
    /// point — a setup that quietly produced a plain directory would leave the test body proving nothing.
    /// </summary>
    public static void Create(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // A JUNCTION, not Directory.CreateSymbolicLink: a Windows symlink needs Developer Mode or an elevated
            // process and a build agent has neither, while `mklink /J` needs no privilege at all. Both are reparse
            // points, and the reparse point is the whole of what redirects a walk.
            using var mklink = Process.Start(
                new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
            mklink.WaitForExit();
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint).Should().BeTrue(
            $"the test needs '{link}' to actually redirect a walk");
    }
}
