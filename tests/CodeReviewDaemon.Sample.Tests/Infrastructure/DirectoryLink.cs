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
                }
            )!;
            mklink.WaitForExit();
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        new DirectoryInfo(link)
            .Attributes.HasFlag(FileAttributes.ReparsePoint)
            .Should()
            .BeTrue($"the test needs '{link}' to actually redirect a walk");
    }

    /// <summary>
    /// Removes every planted link under <paramref name="root"/>, so a recursive delete of the temp tree can
    /// reach the end. <see cref="Directory.Delete(string, bool)"/>'s own recursion THROWS on a Windows junction
    /// rather than removing it, so without this every link-planting test leaves its whole tree behind in the
    /// temp directory. Each delete is non-recursive on purpose: it takes the link and never its target.
    /// </summary>
    public static void UnlinkAllUnder(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var entry in Children(root))
        {
            try
            {
                if (new DirectoryInfo(entry).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(entry);
                    continue;
                }

                UnlinkAllUnder(entry);
            }
            catch
            {
                // Best-effort cleanup only; a stray temp dir must never fail a test.
            }
        }
    }

    private static string[] Children(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch
        {
            return [];
        }
    }
}
