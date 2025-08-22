using MemoryServer.Models;
using MemoryServer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace MemoryServer.Tests.Services;

public class TransportSessionInitializerTests
{
    private readonly Mock<ISessionManager> _mockSessionManager;
    private readonly Mock<ILogger<TransportSessionInitializer>> _mockLogger;
    private readonly MemoryServerOptions _options;
    private readonly TransportSessionInitializer _initializer;

    public TransportSessionInitializerTests()
    {
        _mockSessionManager = new Mock<ISessionManager>();
        _mockLogger = new Mock<ILogger<TransportSessionInitializer>>();

        _options = new MemoryServerOptions
        {
            SessionDefaults = new SessionDefaultsOptions
            {
                DefaultUserId = "default_user",
                MaxSessionAge = 1440 // 24 hours
            }
        };

        var optionsMock = new Mock<IOptions<MemoryServerOptions>>();
        optionsMock.Setup(x => x.Value).Returns(_options);

        _initializer = new TransportSessionInitializer(_mockSessionManager.Object, _mockLogger.Object, optionsMock.Object);
    }

    [Fact]
    public async Task InitializeStdioSessionAsync_WithEnvironmentVariables_ReturnsSessionDefaults()
    {
        Debug.WriteLine("Testing STDIO session initialization with environment variables");

        // Arrange
        var expectedDefaults = new SessionDefaults
        {
            ConnectionId = "stdio-env",
            UserId = "env_user",
            AgentId = "env_agent",
            RunId = "env_run",
            Source = SessionDefaultsSource.EnvironmentVariables
        };

        _mockSessionManager
            .Setup(x => x.ProcessEnvironmentVariablesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDefaults);

        // Act
        var result = await _initializer.InitializeStdioSessionAsync();

        Debug.WriteLine($"Result: {result}");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("stdio-env", result.ConnectionId);
        Assert.Equal("env_user", result.UserId);
        Assert.Equal("env_agent", result.AgentId);
        Assert.Equal("env_run", result.RunId);
        Assert.Equal(SessionDefaultsSource.EnvironmentVariables, result.Source);

        _mockSessionManager.Verify(x => x.ProcessEnvironmentVariablesAsync(It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ STDIO session initialization test passed");
    }

    [Fact]
    public async Task InitializeStdioSessionAsync_WithoutEnvironmentVariables_ReturnsNull()
    {
        Debug.WriteLine("Testing STDIO session initialization without environment variables");

        // Arrange
        _mockSessionManager
            .Setup(x => x.ProcessEnvironmentVariablesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionDefaults?)null);

        // Act
        var result = await _initializer.InitializeStdioSessionAsync();

        Debug.WriteLine($"Result: {result}");

        // Assert
        Assert.Null(result);

        _mockSessionManager.Verify(x => x.ProcessEnvironmentVariablesAsync(It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ STDIO session initialization without env vars test passed");
    }

    [Fact]
    public async Task InitializeSseSessionAsync_WithUrlParametersAndHeaders_ReturnsSessionDefaults()
    {
        Debug.WriteLine("Testing SSE session initialization with URL parameters and headers");

        // Arrange
        var queryParameters = new Dictionary<string, string>
        {
            { "user_id", "url_user" },
            { "agent_id", "url_agent" }
        };

        var headers = new Dictionary<string, string>
        {
            { "X-Memory-User-ID", "header_user" },
            { "X-Memory-Run-ID", "header_run" }
        };

        var expectedDefaults = new SessionDefaults
        {
            ConnectionId = "sse-headers",
            UserId = "header_user", // Headers have higher precedence
            AgentId = "url_agent",
            RunId = "header_run",
            Source = SessionDefaultsSource.HttpHeaders
        };

        _mockSessionManager
            .Setup(x => x.ProcessTransportContextAsync(queryParameters, headers, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDefaults);

        // Act
        var result = await _initializer.InitializeSseSessionAsync(queryParameters, headers);

        Debug.WriteLine($"Result: {result}");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("sse-headers", result.ConnectionId);
        Assert.Equal("header_user", result.UserId); // Headers override URL parameters
        Assert.Equal("url_agent", result.AgentId);
        Assert.Equal("header_run", result.RunId);
        Assert.Equal(SessionDefaultsSource.HttpHeaders, result.Source);

        _mockSessionManager.Verify(x => x.ProcessTransportContextAsync(queryParameters, headers, It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ SSE session initialization test passed");
    }

    [Fact]
    public async Task InitializeSseSessionAsync_WithoutParametersOrHeaders_ReturnsNull()
    {
        Debug.WriteLine("Testing SSE session initialization without parameters or headers");

        // Arrange
        _mockSessionManager
            .Setup(x => x.ProcessTransportContextAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionDefaults?)null);

        // Act
        var result = await _initializer.InitializeSseSessionAsync();

        Debug.WriteLine($"Result: {result}");

        // Assert
        Assert.Null(result);

        _mockSessionManager.Verify(x => x.ProcessTransportContextAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ SSE session initialization without context test passed");
    }

    [Fact]
    public async Task CleanupExpiredSessionsAsync_WithExpiredSessions_ReturnsCleanedCount()
    {
        Debug.WriteLine("Testing cleanup of expired sessions");

        // Arrange
        var expectedCleanedCount = 5;
        var maxAge = TimeSpan.FromMinutes(_options.SessionDefaults.MaxSessionAge);

        _mockSessionManager
            .Setup(x => x.CleanupExpiredSessionsAsync(maxAge, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedCleanedCount);

        // Act
        var result = await _initializer.CleanupExpiredSessionsAsync();

        Debug.WriteLine($"Cleaned up {result} sessions");

        // Assert
        Assert.Equal(expectedCleanedCount, result);

        _mockSessionManager.Verify(x => x.CleanupExpiredSessionsAsync(maxAge, It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ Session cleanup test passed");
    }

    [Fact]
    public void ValidateSessionContext_WithValidDefaults_ReturnsTrue()
    {
        Debug.WriteLine("Testing session context validation with valid defaults");

        // Arrange
        var validDefaults = new SessionDefaults
        {
            ConnectionId = "test-connection",
            UserId = "valid_user",
            AgentId = "valid_agent",
            RunId = "valid_run",
            Source = SessionDefaultsSource.HttpHeaders
        };

        // Act
        var result = _initializer.ValidateSessionContext(validDefaults);

        Debug.WriteLine($"Validation result: {result}");

        // Assert
        Assert.True(result);

        Debug.WriteLine("✅ Valid session context validation test passed");
    }

    [Theory]
    [InlineData(null, "Null session defaults")]
    [InlineData("", "Empty user ID")]
    [InlineData("   ", "Whitespace user ID")]
    public void ValidateSessionContext_WithInvalidUserId_ReturnsFalse(string? userId, string testCase)
    {
        Debug.WriteLine($"Testing session context validation: {testCase}");

        // Arrange
        var invalidDefaults = userId == null ? null : new SessionDefaults
        {
            ConnectionId = "test-connection",
            UserId = userId,
            AgentId = "valid_agent",
            RunId = "valid_run",
            Source = SessionDefaultsSource.HttpHeaders
        };

        // Act
        var result = _initializer.ValidateSessionContext(invalidDefaults);

        Debug.WriteLine($"Validation result: {result}");

        // Assert
        Assert.False(result);

        Debug.WriteLine($"✅ Invalid session context validation test passed: {testCase}");
    }

    [Theory]
    [InlineData(101, "UserId too long")]
    [InlineData(150, "UserId way too long")]
    public void ValidateSessionContext_WithTooLongUserId_ReturnsFalse(int userIdLength, string testCase)
    {
        Debug.WriteLine($"Testing session context validation: {testCase}");

        // Arrange
        var longUserId = new string('a', userIdLength);
        var invalidDefaults = new SessionDefaults
        {
            ConnectionId = "test-connection",
            UserId = longUserId,
            AgentId = "valid_agent",
            RunId = "valid_run",
            Source = SessionDefaultsSource.HttpHeaders
        };

        // Act
        var result = _initializer.ValidateSessionContext(invalidDefaults);

        Debug.WriteLine($"Validation result: {result} for UserId length: {userIdLength}");

        // Assert
        Assert.False(result);

        Debug.WriteLine($"✅ Long UserId validation test passed: {testCase}");
    }

    [Theory]
    [InlineData(101, "AgentId")]
    [InlineData(101, "RunId")]
    public void ValidateSessionContext_WithTooLongOptionalFields_ReturnsFalse(int fieldLength, string fieldName)
    {
        Debug.WriteLine($"Testing session context validation: {fieldName} too long");

        // Arrange
        var longValue = new string('b', fieldLength);
        var invalidDefaults = new SessionDefaults
        {
            ConnectionId = "test-connection",
            UserId = "valid_user",
            Source = SessionDefaultsSource.HttpHeaders
        };

        if (fieldName == "AgentId")
            invalidDefaults.AgentId = longValue;
        else if (fieldName == "RunId")
            invalidDefaults.RunId = longValue;

        // Act
        var result = _initializer.ValidateSessionContext(invalidDefaults);

        Debug.WriteLine($"Validation result: {result} for {fieldName} length: {fieldLength}");

        // Assert
        Assert.False(result);

        Debug.WriteLine($"✅ Long {fieldName} validation test passed");
    }

    [Fact]
    public async Task InitializeStdioSessionAsync_WithException_ReturnsNull()
    {
        Debug.WriteLine("Testing STDIO session initialization with exception");

        // Arrange
        _mockSessionManager
            .Setup(x => x.ProcessEnvironmentVariablesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test exception"));

        // Act
        var result = await _initializer.InitializeStdioSessionAsync();

        Debug.WriteLine($"Result: {result}");

        // Assert
        Assert.Null(result);

        _mockSessionManager.Verify(x => x.ProcessEnvironmentVariablesAsync(It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ STDIO session initialization exception handling test passed");
    }

    [Fact]
    public async Task InitializeSseSessionAsync_WithException_ReturnsNull()
    {
        Debug.WriteLine("Testing SSE session initialization with exception");

        // Arrange
        var queryParameters = new Dictionary<string, string> { { "user_id", "test_user" } };
        var headers = new Dictionary<string, string> { { "X-Memory-User-ID", "header_user" } };

        _mockSessionManager
            .Setup(x => x.ProcessTransportContextAsync(queryParameters, headers, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test exception"));

        // Act
        var result = await _initializer.InitializeSseSessionAsync(queryParameters, headers);

        Debug.WriteLine($"Result: {result}");

        // Assert
        Assert.Null(result);

        _mockSessionManager.Verify(x => x.ProcessTransportContextAsync(queryParameters, headers, It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ SSE session initialization exception handling test passed");
    }

    [Fact]
    public async Task CleanupExpiredSessionsAsync_WithException_ReturnsZero()
    {
        Debug.WriteLine("Testing session cleanup with exception");

        // Arrange
        var maxAge = TimeSpan.FromMinutes(_options.SessionDefaults.MaxSessionAge);

        _mockSessionManager
            .Setup(x => x.CleanupExpiredSessionsAsync(maxAge, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test exception"));

        // Act
        var result = await _initializer.CleanupExpiredSessionsAsync();

        Debug.WriteLine($"Cleanup result: {result}");

        // Assert
        Assert.Equal(0, result);

        _mockSessionManager.Verify(x => x.CleanupExpiredSessionsAsync(maxAge, It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ Session cleanup exception handling test passed");
    }
}