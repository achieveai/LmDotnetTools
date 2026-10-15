using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmStreaming.Sample.FileBrowser;

namespace LmStreaming.Sample.Tests.FileBrowser;

/// <summary>
/// Runs <see cref="WorkspaceAssetReader.ReadScript"/> against a real Linux directory tree, so the
/// no-follow, traversal and size guarantees are proven by the kernel rather than by a fake. The
/// script's fixed <c>/workspace</c> root is pointed at a temporary directory; nothing else changes.
/// Linux-only: O_NOFOLLOW directory walks with <c>dir_fd</c> do not exist on Windows.
/// </summary>
public sealed class WorkspaceAssetReaderScriptTests : IDisposable
{
    private static readonly byte[] Page = [.. "<h1>ok</h1>"u8, 0, 255, 128];
    private readonly string _workspace = Directory.CreateTempSubdirectory("asset-reader-").FullName;

    public WorkspaceAssetReaderScriptTests()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var reports = Directory.CreateDirectory(Path.Combine(_workspace, "reports")).FullName;
        File.WriteAllBytes(Path.Combine(reports, "index.html"), Page);
        File.WriteAllBytes(Path.Combine(reports, "big.bin"), new byte[11]);
        Directory.CreateDirectory(Path.Combine(reports, "sub"));
        File.WriteAllText(Path.Combine(_workspace, "secret.txt"), "secret");
        File.CreateSymbolicLink(Path.Combine(reports, "link.html"), "index.html");
        File.CreateSymbolicLink(Path.Combine(reports, "escape.txt"), "../secret.txt");
        Directory.CreateSymbolicLink(Path.Combine(_workspace, "linked-dir"), "reports");
    }

    [LinuxOnlyFact("the reader script walks directories with O_NOFOLLOW handles and dir_fd")]
    public async Task UnsafeOrOversizedTargets_ExitWithTheMappedCodeAndNoBytes()
    {
        (string Root, string Relative, long Limit, int ExitCode)[] cases =
        [
            ("reports", "link.html", 1024, 44),
            ("reports", "escape.txt", 1024, 44),
            ("linked-dir", "index.html", 1024, 44),
            ("", "linked-dir/index.html", 1024, 44),
            ("reports", "../secret.txt", 1024, 44),
            ("reports", "missing.html", 1024, 44),
            ("reports", "sub", 1024, 44),
            ("reports", "big.bin", 10, 45),
        ];
        foreach (var (root, relative, limit, exitCode) in cases)
        {
            var (code, stdout) = await RunAsync(root, relative, limit);

            code.Should().Be(exitCode, $"root '{root}' + '{relative}' must be refused");
            stdout.Should().BeEmpty($"root '{root}' + '{relative}' must not leak bytes");
        }
    }

    [LinuxOnlyFact("the reader script walks directories with O_NOFOLLOW handles and dir_fd")]
    public async Task RegularFile_UnderTheRoot_ReturnsExactBytes()
    {
        foreach (var (root, relative) in new[] { ("reports", "index.html"), ("", "reports/index.html") })
        {
            var (code, stdout) = await RunAsync(root, relative, Page.Length);

            code.Should().Be(0, $"root '{root}' + '{relative}' is a regular file under the root");
            stdout.Should().Equal(Page);
        }
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private async Task<(int ExitCode, byte[] Stdout)> RunAsync(string root, string relative, long limit)
    {
        var script = WorkspaceAssetReader.ReadScript.Replace(
            "'/workspace'",
            $"'{_workspace}'",
            StringComparison.Ordinal
        );
        script.Should().NotBe(WorkspaceAssetReader.ReadScript, "the test must redirect the workspace root");
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (
            var argument in new[]
            {
                "-I",
                "-S",
                "-c",
                script,
                root,
                relative,
                limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }
        )
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await copy;
        (await stderr).Should().BeEmpty();
        return (process.ExitCode, stdout.ToArray());
    }
}
