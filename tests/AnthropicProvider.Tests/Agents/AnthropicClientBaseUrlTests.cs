using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

/// <summary>
///     Tests for AnthropicClient base URL configurability.
///     Verifies URL resolution order: explicit param > ANTHROPIC_BASE_URL env var > default.
/// </summary>
public class AnthropicClientBaseUrlTests : LoggingTestBase
{
    public AnthropicClientBaseUrlTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task DefaultUrl_ShouldUseApiAnthropicV1()
    {
        Logger.LogInformation("Testing default URL resolution");

        // Arrange - Temporarily unset ANTHROPIC_BASE_URL to test default behavior
        var originalEnvValue = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", null);

            var requestCapture = new RequestCapture();
            var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
                LoggerFactory,
                requestCapture,
                chunkDelayMs: 0
            );

            var client = new AnthropicClient("test-api-key", httpClient: httpClient);
            var agent = new AnthropicAgent("TestAgent", client);

            // Act
            var responses = await agent.GenerateReplyAsync(
                [new TextMessage { Role = Role.User, Text = "Hello" }],
                new GenerateReplyOptions { ModelId = "claude-3-sonnet-20240229" }
            );

            // Assert
            Assert.True(requestCapture.WasSentTo("/v1/messages"),
                $"Request should be sent to /v1/messages. Actual URI: {requestCapture.LastRequest?.RequestUri}");

            var requestUri = requestCapture.LastRequest?.RequestUri?.ToString();
            Logger.LogInformation("Default URL request URI: {RequestUri}", requestUri);
            Assert.Contains("api.anthropic.com/v1/messages", requestUri!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", originalEnvValue);
        }
    }

    [Fact]
    public async Task ExplicitBaseUrl_ShouldOverrideDefault()
    {
        Logger.LogInformation("Testing explicit base URL parameter");

        // Arrange
        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory,
            requestCapture,
            chunkDelayMs: 0
        );

        // Use explicit base URL
        var client = new AnthropicClient(
            "test-api-key",
            baseUrl: "https://custom-api.example.com/v1",
            httpClient: httpClient
        );
        var agent = new AnthropicAgent("TestAgent", client);

        // Act
        var responses = await agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = "Hello" }],
            new GenerateReplyOptions { ModelId = "claude-3-sonnet-20240229" }
        );

        // Assert
        var requestUri = requestCapture.LastRequest?.RequestUri?.ToString();
        Logger.LogInformation("Explicit URL request URI: {RequestUri}", requestUri);
        Assert.Contains("custom-api.example.com/v1/messages", requestUri!);
    }

    [Fact]
    public async Task ExplicitBaseUrl_WithoutV1_ShouldBeUsedAsIs()
    {
        Logger.LogInformation("Testing explicit base URL without /v1 suffix is used as-is");

        // Arrange
        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory,
            requestCapture,
            chunkDelayMs: 0
        );

        // Use explicit base URL without /v1 - should be used as-is (no auto-normalization)
        var client = new AnthropicClient(
            "test-api-key",
            baseUrl: "https://custom-api.example.com",
            httpClient: httpClient
        );
        var agent = new AnthropicAgent("TestAgent", client);

        // Act
        var responses = await agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = "Hello" }],
            new GenerateReplyOptions { ModelId = "claude-3-sonnet-20240229" }
        );

        // Assert - URL is used as-is, /messages appended directly
        var requestUri = requestCapture.LastRequest?.RequestUri?.ToString();
        Logger.LogInformation("As-is URL request URI: {RequestUri}", requestUri);
        Assert.Contains("custom-api.example.com/messages", requestUri!);
    }

    [Fact]
    public async Task EnvVarBaseUrl_ShouldOverrideDefault()
    {
        Logger.LogInformation("Testing ANTHROPIC_BASE_URL env var override");

        // Arrange - Set env var to a custom URL
        var originalEnvValue = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://env-custom-api.example.com/v1");

            var requestCapture = new RequestCapture();
            var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
                LoggerFactory,
                requestCapture,
                chunkDelayMs: 0
            );

            var client = new AnthropicClient("test-api-key", httpClient: httpClient);
            var agent = new AnthropicAgent("TestAgent", client);

            // Act
            var responses = await agent.GenerateReplyAsync(
                [new TextMessage { Role = Role.User, Text = "Hello" }],
                new GenerateReplyOptions { ModelId = "claude-3-sonnet-20240229" }
            );

            // Assert
            var requestUri = requestCapture.LastRequest?.RequestUri?.ToString();
            Logger.LogInformation("Env var URL request URI: {RequestUri}", requestUri);
            Assert.Contains("env-custom-api.example.com/v1/messages", requestUri!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", originalEnvValue);
        }
    }

    [Fact]
    public async Task EnvVarBaseUrl_WithoutV1_ShouldBeUsedAsIs()
    {
        Logger.LogInformation("Testing ANTHROPIC_BASE_URL env var without /v1 suffix is used as-is");

        // Arrange
        var originalEnvValue = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://env-no-v1.example.com");

            var requestCapture = new RequestCapture();
            var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
                LoggerFactory,
                requestCapture,
                chunkDelayMs: 0
            );

            var client = new AnthropicClient("test-api-key", httpClient: httpClient);
            var agent = new AnthropicAgent("TestAgent", client);

            // Act
            var responses = await agent.GenerateReplyAsync(
                [new TextMessage { Role = Role.User, Text = "Hello" }],
                new GenerateReplyOptions { ModelId = "claude-3-sonnet-20240229" }
            );

            // Assert - URL is used as-is, /messages appended directly
            var requestUri = requestCapture.LastRequest?.RequestUri?.ToString();
            Logger.LogInformation("Env var as-is URL request URI: {RequestUri}", requestUri);
            Assert.Contains("env-no-v1.example.com/messages", requestUri!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", originalEnvValue);
        }
    }

    [Fact]
    public async Task ExplicitBaseUrl_ShouldTakePriorityOverEnvVar()
    {
        Logger.LogInformation("Testing explicit URL takes priority over env var");

        // Arrange
        var originalEnvValue = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://env-api.example.com/v1");

            var requestCapture = new RequestCapture();
            var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
                LoggerFactory,
                requestCapture,
                chunkDelayMs: 0
            );

            // Explicit URL should override env var
            var client = new AnthropicClient(
                "test-api-key",
                baseUrl: "https://explicit-api.example.com/v1",
                httpClient: httpClient
            );
            var agent = new AnthropicAgent("TestAgent", client);

            // Act
            var responses = await agent.GenerateReplyAsync(
                [new TextMessage { Role = Role.User, Text = "Hello" }],
                new GenerateReplyOptions { ModelId = "claude-3-sonnet-20240229" }
            );

            // Assert
            var requestUri = requestCapture.LastRequest?.RequestUri?.ToString();
            Logger.LogInformation("Priority test request URI: {RequestUri}", requestUri);
            Assert.Contains("explicit-api.example.com/v1/messages", requestUri!);
            Assert.DoesNotContain("env-api.example.com", requestUri!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", originalEnvValue);
        }
    }

    [Fact]
    public async Task ExplicitBaseUrl_WithCustomPath_ShouldPreservePath()
    {
        Logger.LogInformation("Testing explicit base URL with custom path prefix (e.g. Kimi)");

        // Arrange
        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory,
            requestCapture,
            chunkDelayMs: 0
        );

        // URL like Kimi uses: custom path without /v1
        var client = new AnthropicClient(
            "test-api-key",
            baseUrl: "https://api.kimi.com/coding",
            httpClient: httpClient
        );
        var agent = new AnthropicAgent("TestAgent", client);

        // Act
        var responses = await agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = "Hello" }],
            new GenerateReplyOptions { ModelId = "kimi-2.5" }
        );

        // Assert - path should be preserved, /messages appended
        var requestUri = requestCapture.LastRequest?.RequestUri?.ToString();
        Logger.LogInformation("Custom path URL request URI: {RequestUri}", requestUri);
        Assert.Contains("api.kimi.com/coding/messages", requestUri!);
    }

    [Fact]
    public async Task RequestUrlEcho_ShouldReturnActualRequestUrl()
    {
        Logger.LogInformation("Testing request_url_echo integration with base URL");

        // Arrange
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory,
            chunkDelayMs: 0
        );

        var client = new AnthropicClient(
            "test-api-key",
            baseUrl: "https://kimi-test.example.com/v1",
            httpClient: httpClient
        );
        var agent = new AnthropicAgent("TestAgent", client);

        // Use instruction chain with request_url_echo
        var userMessage = """
            Hello
            <|instruction_start|>
            {"instruction_chain":[{"id_message":"url_echo","messages":[{"request_url_echo":{}}]}]}
            <|instruction_end|>
            """;

        // Act
        var responses = await agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = userMessage }],
            new GenerateReplyOptions { ModelId = "claude-3-sonnet-20240229" }
        );

        // Assert
        var response = responses?.FirstOrDefault();
        Assert.NotNull(response);
        var textResponse = Assert.IsType<TextMessage>(response);
        Logger.LogInformation("URL echo response: {Response}", textResponse.Text);
        Assert.Contains("kimi-test.example.com/v1/messages", textResponse.Text);
    }

    [Fact]
    public async Task RequestHeadersEcho_ShouldReturnHeaders()
    {
        Logger.LogInformation("Testing request_headers_echo with Anthropic client");

        // Arrange
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory,
            chunkDelayMs: 0
        );

        var client = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", client);

        // Use instruction chain with request_headers_echo
        var userMessage = """
            Hello
            <|instruction_start|>
            {"instruction_chain":[{"id_message":"headers_echo","messages":[{"request_headers_echo":{}}]}]}
            <|instruction_end|>
            """;

        // Act
        var responses = await agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = userMessage }],
            new GenerateReplyOptions { ModelId = "claude-3-sonnet-20240229" }
        );

        // Assert
        var response = responses?.FirstOrDefault();
        Assert.NotNull(response);
        var textResponse = Assert.IsType<TextMessage>(response);
        Logger.LogInformation("Headers echo response: {Response}", textResponse.Text);
        Assert.Contains("Content-Type", textResponse.Text);
    }

    [Fact]
    public async Task HttpClientConstructor_WithExplicitBaseUrl_ShouldWork()
    {
        Logger.LogInformation("Testing HttpClient constructor with explicit base URL");

        // Arrange
        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory,
            requestCapture,
            chunkDelayMs: 0
        );

        // Use the HttpClient constructor with explicit base URL
        var client = new AnthropicClient(
            httpClient,
            baseUrl: "https://httpclient-ctor.example.com/v1",
            logger: LoggerFactory.CreateLogger<AnthropicClient>()
        );

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 100,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContent { Type = "text", Text = "Hello" }],
                },
            ],
        };

        // Act
        var response = await client.CreateChatCompletionsAsync(request);

        // Assert
        var requestUri = requestCapture.LastRequest?.RequestUri?.ToString();
        Logger.LogInformation("HttpClient ctor URL: {RequestUri}", requestUri);
        Assert.Contains("httpclient-ctor.example.com/v1/messages", requestUri!);
    }
}
