using AchieveAi.LmDotnetTools.CodexSdkProvider.Bootstrap;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Bootstrap;

public class CodexBridgeDependencyInstallerTests
{
    [Fact]
    public void IsInstallSatisfied_ReturnsFalse_WhenNodeModulesMissing()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "package-lock.json"), "{}");

            CodexBridgeDependencyInstaller.IsInstallSatisfied(tempDir.FullName).Should().BeFalse();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsInstallSatisfied_ReturnsTrue_WhenPinnedVersionExists()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var sdkPath = Path.Combine(tempDir.FullName, "node_modules", "@openai", "codex-sdk");
            Directory.CreateDirectory(sdkPath);
            File.WriteAllText(Path.Combine(tempDir.FullName, "package-lock.json"), "{}");
            File.WriteAllText(
                Path.Combine(sdkPath, "package.json"),
                "{\"name\":\"@openai/codex-sdk\",\"version\": \"0.101.0\"}");

            CodexBridgeDependencyInstaller.IsInstallSatisfied(tempDir.FullName).Should().BeTrue();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EnsureInstalledAsync_EmitsStructuredLogs_OnFailure()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var logger = new TestLogger();
        try
        {
            var installer = new CodexBridgeDependencyInstaller();
            var act = async () => await installer.EnsureInstalledAsync(
                tempDir.FullName,
                npmPath: "/path/that/does/not/exist/npm",
                logger,
                CancellationToken.None);

            await act.Should().ThrowAsync<Exception>();

            logger.Messages.Should().Contain(m => m.Contains("codex.dependency.install.started", StringComparison.Ordinal));
            logger.Messages.Should().Contain(m => m.Contains("codex.dependency.install.failed", StringComparison.Ordinal));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private sealed class TestLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
