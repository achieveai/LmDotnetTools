namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Builds the one redirected path that reads as ABSENT: a symlink whose name stands where a directory should be,
/// but whose own entry is a file.
/// </summary>
internal static class FileLink
{
    /// <summary>
    /// Whether this process can create a file symlink at all. Windows gates symlink creation behind Developer
    /// Mode or an elevated process — the same reason <see cref="DirectoryLink"/> uses <c>mklink /J</c> — and a
    /// junction cannot stand in here, because a junction always reads as a directory and that is precisely the
    /// property under test. There is no privilege-free substitute, so the capability is probed once and the test
    /// that needs it reports itself skipped rather than passing for the wrong reason.
    /// </summary>
    public static bool Supported { get; } = Probe();

    /// <summary>
    /// Points <paramref name="link"/> at <paramref name="target"/> as a FILE symlink, then asserts the result
    /// both redirects and reads as a non-directory — a setup that quietly produced either a plain file or a
    /// directory link would leave the test body proving nothing.
    /// </summary>
    public static void Create(string link, string target)
    {
        _ = File.CreateSymbolicLink(link, target);

        var entry = new FileInfo(link);
        entry
            .Attributes.HasFlag(FileAttributes.ReparsePoint)
            .Should()
            .BeTrue($"the test needs '{link}' to actually redirect");
        Directory
            .Exists(link)
            .Should()
            .BeFalse($"the test needs '{link}' to read as absent to a directory-existence check");
    }

    private static bool Probe()
    {
        var probeRoot = Path.Combine(Path.GetTempPath(), $"crd-symlink-probe-{Guid.NewGuid():N}");
        try
        {
            _ = Directory.CreateDirectory(probeRoot);
            var target = Path.Combine(probeRoot, "target");
            File.WriteAllText(target, "probe");
            _ = File.CreateSymbolicLink(Path.Combine(probeRoot, "link"), target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(probeRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A probe that cannot clean up after itself still answered the question it was asked.
            }
        }
    }
}
