using AchieveAi.LmDotnetTools.Misc.Configuration;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Configuration;

public class WebToolsOptionsTests
{
    [Fact]
    public void Defaults_ShouldMatchSpecifiedValues()
    {
        // Act
        var options = new WebToolsOptions();

        // Assert
        options.Backend.Should().Be("jina");
        options.JinaApiKey.Should().BeNull();
        options.OutputCap.Should().Be(50_000);
        options.TimeoutMs.Should().Be(30_000);
        options.MaxQueryLength.Should().Be(2_048);
    }

    [Fact]
    public void Validate_WithDefaults_ShouldReturnNoErrors()
    {
        // Arrange
        var options = new WebToolsOptions();

        // Act & Assert
        options.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_WithUnknownBackend_ShouldReturnError()
    {
        // Arrange
        var options = new WebToolsOptions { Backend = "bing" };

        // Act
        var errors = options.Validate();

        // Assert
        errors.Should().ContainSingle().Which.Should().Contain("bing").And.Contain("jina");
    }

    [Fact]
    public void Validate_WithNonPositiveOutputCap_ShouldReturnError()
    {
        // Arrange
        var options = new WebToolsOptions { OutputCap = 0 };

        // Act & Assert
        options.Validate().Should().ContainSingle().Which.Should().Contain("OutputCap");
    }

    [Fact]
    public void Validate_WithNonPositiveTimeout_ShouldReturnError()
    {
        // Arrange
        var options = new WebToolsOptions { TimeoutMs = -1 };

        // Act & Assert
        options.Validate().Should().ContainSingle().Which.Should().Contain("TimeoutMs");
    }

    [Fact]
    public void Validate_WithNonPositiveMaxQueryLength_ShouldReturnError()
    {
        // Arrange
        var options = new WebToolsOptions { MaxQueryLength = 0 };

        // Act & Assert
        options.Validate().Should().ContainSingle().Which.Should().Contain("MaxQueryLength");
    }

    [Fact]
    public void FromEnvironment_ShouldReadKnownVariables()
    {
        // Arrange
        var original = (
            backend: Environment.GetEnvironmentVariable("WEB_TOOLS_BACKEND"),
            key: Environment.GetEnvironmentVariable("JINA_API_KEY"),
            cap: Environment.GetEnvironmentVariable("WEB_TOOLS_OUTPUT_CAP"),
            timeout: Environment.GetEnvironmentVariable("WEB_TOOLS_TIMEOUT_MS")
        );

        try
        {
            Environment.SetEnvironmentVariable("WEB_TOOLS_BACKEND", "jina");
            Environment.SetEnvironmentVariable("JINA_API_KEY", "test-key-123");
            Environment.SetEnvironmentVariable("WEB_TOOLS_OUTPUT_CAP", "1234");
            Environment.SetEnvironmentVariable("WEB_TOOLS_TIMEOUT_MS", "5678");

            // Act
            var options = WebToolsOptions.FromEnvironment();

            // Assert
            options.Backend.Should().Be("jina");
            options.JinaApiKey.Should().Be("test-key-123");
            options.OutputCap.Should().Be(1234);
            options.TimeoutMs.Should().Be(5678);
            options.MaxQueryLength.Should().Be(2_048);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEB_TOOLS_BACKEND", original.backend);
            Environment.SetEnvironmentVariable("JINA_API_KEY", original.key);
            Environment.SetEnvironmentVariable("WEB_TOOLS_OUTPUT_CAP", original.cap);
            Environment.SetEnvironmentVariable("WEB_TOOLS_TIMEOUT_MS", original.timeout);
        }
    }

    [Fact]
    public void FromEnvironment_WithInvalidNumbers_ShouldFallBackToDefaults()
    {
        // Arrange
        var originalCap = Environment.GetEnvironmentVariable("WEB_TOOLS_OUTPUT_CAP");
        var originalTimeout = Environment.GetEnvironmentVariable("WEB_TOOLS_TIMEOUT_MS");

        try
        {
            Environment.SetEnvironmentVariable("WEB_TOOLS_OUTPUT_CAP", "not-a-number");
            Environment.SetEnvironmentVariable("WEB_TOOLS_TIMEOUT_MS", "-5");

            // Act
            var options = WebToolsOptions.FromEnvironment();

            // Assert
            options.OutputCap.Should().Be(50_000);
            options.TimeoutMs.Should().Be(30_000);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEB_TOOLS_OUTPUT_CAP", originalCap);
            Environment.SetEnvironmentVariable("WEB_TOOLS_TIMEOUT_MS", originalTimeout);
        }
    }
}
