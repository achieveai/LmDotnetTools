using System.Runtime.InteropServices;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Agents;

public class CopilotCliPathResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrEmpty_ReturnsInput(string? input)
    {
        CopilotCliPathResolver.Resolve(input!).Should().Be(input);
    }

    [Fact]
    public void Resolve_BareName_ReturnsUnchanged()
    {
        // Bare "copilot" must flow through Process.Start unchanged so PATH+PATHEXT applies.
        CopilotCliPathResolver.Resolve("copilot").Should().Be("copilot");
    }

    [Fact]
    public void Resolve_Windows_ExtensionlessPath_ProbesCmdWrapper()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Windows-only behavior; no-op elsewhere.
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"copilot-resolver-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(tempDir);
        try
        {
            var extensionless = Path.Combine(tempDir, "copilot");
            var cmdWrapper = Path.Combine(tempDir, "copilot.cmd");
            File.WriteAllText(cmdWrapper, "@echo off");

            CopilotCliPathResolver.Resolve(extensionless).Should().Be(cmdWrapper);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_Windows_ExtensionlessPath_PrefersCmdOverExe()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"copilot-resolver-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(tempDir);
        try
        {
            var extensionless = Path.Combine(tempDir, "copilot");
            var cmdWrapper = Path.Combine(tempDir, "copilot.cmd");
            var exeWrapper = Path.Combine(tempDir, "copilot.exe");
            File.WriteAllText(cmdWrapper, "@echo off");
            File.WriteAllText(exeWrapper, "fake");

            CopilotCliPathResolver.Resolve(extensionless).Should().Be(cmdWrapper);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_Windows_PathWithExtensionThatExists_ReturnsUnchanged()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"copilot-resolver-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(tempDir);
        try
        {
            var cmdWrapper = Path.Combine(tempDir, "copilot.cmd");
            File.WriteAllText(cmdWrapper, "@echo off");

            CopilotCliPathResolver.Resolve(cmdWrapper).Should().Be(cmdWrapper);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_Windows_ExtensionlessPath_NoWrapperExists_ReturnsOriginal()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var nonExistent = Path.Combine(Path.GetTempPath(), $"copilot-{Guid.NewGuid():N}", "copilot");
        CopilotCliPathResolver.Resolve(nonExistent).Should().Be(nonExistent);
    }
}
