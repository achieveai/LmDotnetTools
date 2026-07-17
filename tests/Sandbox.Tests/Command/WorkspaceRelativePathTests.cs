using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class WorkspaceRelativePathTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("sub", "sub")]
    [InlineData("sub/dir", "sub/dir")]
    [InlineData("./a/./b", "a/b")]
    [InlineData("a//b", "a/b")]
    [InlineData("a/b/", "a/b")]
    [InlineData(".", "")]
    [InlineData("a/b/c.txt", "a/b/c.txt")]
    public void Normalize_AcceptsAndNormalizesWorkspaceRelativePaths(string? input, string expected)
    {
        WorkspaceRelativePath.Normalize(input, "workingDirectory").Should().Be(expected);
    }

    [Theory]
    // POSIX absolute roots.
    [InlineData("/")]
    [InlineData("/etc/passwd")]
    // Windows drive roots (forward-slash and drive-relative; the backslash form is caught below).
    [InlineData("C:")]
    [InlineData("C:/Windows")]
    [InlineData("c:relative")]
    // Backslash-bearing: Windows drive, UNC, device, and mixed-separator traversal.
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"a\b")]
    [InlineData(@"a\..\b")]
    // Lexical parent-directory escape (any '..' segment is refused).
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData("a/../../etc")]
    [InlineData("a/../b")]
    // NUL.
    [InlineData("a\0b")]
    public void Normalize_RejectsRootedDriveUncDeviceTraversalAndNul(string input)
    {
        var act = () => WorkspaceRelativePath.Normalize(input, "workingDirectory");

        act.Should().Throw<ArgumentException>().WithParameterName("workingDirectory");
    }
}
