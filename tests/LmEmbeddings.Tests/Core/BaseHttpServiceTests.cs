using System.Diagnostics;
using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmTestUtils;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmEmbeddings.Tests.Core;

/// <summary>
///     Tests for BaseHttpService functionality including disposal, retry operations, and common infrastructure
/// </summary>
public class BaseHttpServiceTests
{
    private readonly ILogger<TestHttpService> _logger;

    public BaseHttpServiceTests()
    {
        _logger = TestLoggerFactory.CreateLogger<TestHttpService>();
    }

    #region Test Service Implementation

    /// <summary>
    ///     Test implementation of BaseHttpService for testing purposes
    /// </summary>
    public class TestHttpService : BaseHttpService
    {
        public TestHttpService(ILogger<TestHttpService> logger, HttpClient httpClient)
            : base(logger, httpClient) { }

        // Expose protected members for testing
        public ILogger PublicLogger => Logger;
        public HttpClient PublicHttpClient => HttpClient;

        public void TestThrowIfDisposed()
        {
            ThrowIfDisposed();
        }

        public async Task<T> TestExecuteWithRetryAsync<T>(
            Func<Task<T>> operation,
            int maxRetries = 2,
            CancellationToken cancellationToken = default
        )
        {
            // Use fast retry options for tests
            var options = new RetryOptions
            {
                MaxRetries = maxRetries,
                InitialDelayMs = 100,
                MaxDelayMs = 500,
                BackoffMultiplier = 2.0,
            };
            return await ExecuteWithRetryAsync(operation, options, cancellationToken);
        }

        public async Task<T> TestExecuteHttpWithRetryAsync<T>(
            Func<Task<HttpResponseMessage>> httpOperation,
            Func<HttpResponseMessage, Task<T>> responseProcessor,
            int maxRetries = 2,
            CancellationToken cancellationToken = default
        )
        {
            // Use fast retry options for tests
            var options = new RetryOptions
            {
                MaxRetries = maxRetries,
                InitialDelayMs = 100,
                MaxDelayMs = 500,
                BackoffMultiplier = 2.0,
            };
            return await ExecuteHttpWithRetryAsync(httpOperation, responseProcessor, options, cancellationToken);
        }
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidParameters_InitializesCorrectly()
    {
        Debug.WriteLine("Testing BaseHttpService constructor with valid parameters");

        // Arrange
        var httpClient = new HttpClient { BaseAddress = new Uri("https://api.test.com") };

        // Act
        var service = new TestHttpService(_logger, httpClient);

        // Assert
        Assert.NotNull(service);
        Assert.NotNull(service.PublicLogger);
        Assert.NotNull(service.PublicHttpClient);
        Assert.Equal("https://api.test.com/", service.PublicHttpClient.BaseAddress?.ToString());
        Debug.WriteLine("✓ Constructor initialized service correctly with valid parameters");
    }

    [Theory]
    [MemberData(nameof(ConstructorInvalidParametersTestCases))]
    public void Constructor_WithInvalidParameters_ThrowsArgumentNullException(
        ILogger<TestHttpService>? logger,
        HttpClient? httpClient,
        string expectedParameterName,
        string description
    )
    {
        Debug.WriteLine($"Testing constructor with invalid parameters: {description}");

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => new TestHttpService(logger!, httpClient!));

        Assert.Equal(expectedParameterName, exception.ParamName);
        Debug.WriteLine($"✓ Expected ArgumentNullException thrown for: {description}");
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public void Dispose_WhenCalled_SetsDisposedState()
    {
        Debug.WriteLine("Testing disposal sets disposed state correctly");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);

        // Act
        service.Dispose();

        // Assert
        _ = Assert.Throws<ObjectDisposedException>(service.TestThrowIfDisposed);
        Debug.WriteLine("✓ Service correctly disposed and throws ObjectDisposedException");
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        Debug.WriteLine("Testing multiple dispose calls do not throw");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);

        // Act & Assert
        service.Dispose();
        service.Dispose(); // Should not throw
        service.Dispose(); // Should not throw

        Debug.WriteLine("✓ Multiple dispose calls handled gracefully");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_AfterDisposal_ThrowsObjectDisposedException()
    {
        Debug.WriteLine("Testing ExecuteWithRetryAsync throws after disposal");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);
        service.Dispose();

        // Act & Assert
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.TestExecuteWithRetryAsync(() => Task.FromResult("test"))
        );

        Debug.WriteLine("✓ ExecuteWithRetryAsync correctly throws ObjectDisposedException after disposal");
    }

    [Fact]
    public async Task ExecuteHttpWithRetryAsync_AfterDisposal_ThrowsObjectDisposedException()
    {
        Debug.WriteLine("Testing ExecuteHttpWithRetryAsync throws after disposal");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);
        service.Dispose();

        // Act & Assert
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.TestExecuteHttpWithRetryAsync(
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
                _ => Task.FromResult("test")
            )
        );

        Debug.WriteLine("✓ ExecuteHttpWithRetryAsync correctly throws ObjectDisposedException after disposal");
    }

    #endregion

    #region Retry Functionality Tests

    [Fact]
    public async Task ExecuteWithRetryAsync_WithSuccessfulOperation_ReturnsResult()
    {
        Debug.WriteLine("Testing ExecuteWithRetryAsync with successful operation");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);
        const string expectedResult = "success";

        // Act
        var result = await service.TestExecuteWithRetryAsync(() => Task.FromResult(expectedResult));

        // Assert
        Assert.Equal(expectedResult, result);
        Debug.WriteLine($"✓ ExecuteWithRetryAsync returned expected result: {result}");
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task ExecuteWithRetryAsync_WithRetryableFailure_RetriesAndSucceeds()
    {
        Debug.WriteLine("Testing ExecuteWithRetryAsync with retryable failure then success");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);
        var attempts = 0;

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await service.TestExecuteWithRetryAsync(() =>
        {
            attempts++;
            // Fail on first attempt, succeed on second (1 retry)
            return attempts < 2
                ? throw new HttpRequestException("Temporary network timeout")
                : Task.FromResult("success");
        });
        stopwatch.Stop();

        // Assert
        Assert.Equal("success", result);
        Assert.Equal(2, attempts);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 80,
            $"Expected at least 80ms for 1 retry (100ms configured), got {stopwatch.ElapsedMilliseconds}ms"
        );
        Debug.WriteLine(
            $"✓ ExecuteWithRetryAsync succeeded after {attempts} attempts in {stopwatch.ElapsedMilliseconds}ms"
        );
    }

    [Fact]
    public async Task ExecuteHttpWithRetryAsync_WithSuccessfulResponse_ReturnsProcessedResult()
    {
        Debug.WriteLine("Testing ExecuteHttpWithRetryAsync with successful HTTP response");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);
        const string expectedContent = "test content";

        // Act
        var result = await service.TestExecuteHttpWithRetryAsync(
            () =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(expectedContent) }
                ),
            async response =>
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return await response.Content.ReadAsStringAsync();
            }
        );

        // Assert
        Assert.Equal(expectedContent, result);
        Debug.WriteLine($"✓ ExecuteHttpWithRetryAsync processed HTTP response correctly: {result}");
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task ExecuteHttpWithRetryAsync_WithRetryableHttpFailure_RetriesAndSucceeds()
    {
        Debug.WriteLine("Testing ExecuteHttpWithRetryAsync with retryable HTTP failure then success");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);
        var attempts = 0;

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await service.TestExecuteHttpWithRetryAsync(
            () =>
            {
                attempts++;
                // Fail on first attempt, succeed on second (1 retry)
                return attempts < 2
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
                    : Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("success") }
                    );
            },
            async response =>
            {
                return response.StatusCode == HttpStatusCode.InternalServerError
                    ? throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.StatusCode}")
                    : await response.Content.ReadAsStringAsync();
            }
        );
        stopwatch.Stop();

        // Assert
        Assert.Equal("success", result);
        Assert.Equal(2, attempts);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 80,
            $"Expected at least 80ms for 1 retry (100ms configured), got {stopwatch.ElapsedMilliseconds}ms"
        );
        Debug.WriteLine(
            $"✓ ExecuteHttpWithRetryAsync succeeded after {attempts} attempts in {stopwatch.ElapsedMilliseconds}ms"
        );
    }

    [Theory]
    [MemberData(nameof(RetryParametersTestCases))]
    public async Task ExecuteWithRetryAsync_WithCustomMaxRetries_RespectsMaxRetries(
        int maxRetries,
        int expectedAttempts,
        string description
    )
    {
        Debug.WriteLine($"Testing ExecuteWithRetryAsync with custom max retries: {description}");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);
        var attempts = 0;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.TestExecuteWithRetryAsync<string>(
                () =>
                {
                    attempts++;
                    throw new HttpRequestException("network timeout error");
                },
                maxRetries
            )
        );

        Assert.Equal(expectedAttempts, attempts);
        Debug.WriteLine($"✓ ExecuteWithRetryAsync made {attempts} attempts with maxRetries={maxRetries}");
    }

    #endregion

    #region ThrowIfDisposed Tests

    [Fact]
    public void ThrowIfDisposed_WhenNotDisposed_DoesNotThrow()
    {
        Debug.WriteLine("Testing ThrowIfDisposed when not disposed");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);

        // Act & Assert
        service.TestThrowIfDisposed(); // Should not throw

        Debug.WriteLine("✓ ThrowIfDisposed did not throw when service was not disposed");
    }

    [Fact]
    public void ThrowIfDisposed_WhenDisposed_ThrowsObjectDisposedException()
    {
        Debug.WriteLine("Testing ThrowIfDisposed when disposed");

        // Arrange
        var httpClient = new HttpClient();
        var service = new TestHttpService(_logger, httpClient);
        service.Dispose();

        // Act & Assert
        var exception = Assert.Throws<ObjectDisposedException>(service.TestThrowIfDisposed);
        Assert.Equal(nameof(TestHttpService), exception.ObjectName);

        Debug.WriteLine("✓ ThrowIfDisposed correctly threw ObjectDisposedException when disposed");
    }

    #endregion

    #region Test Data

    public static IEnumerable<object[]> ConstructorInvalidParametersTestCases =>
        [
            [null!, new HttpClient(), "logger", "Null logger"],
            [TestLoggerFactory.CreateLogger<TestHttpService>(), null!, "httpClient", "Null HttpClient"],
        ];

    public static IEnumerable<object[]> RetryParametersTestCases =>
        [
            [0, 1, "No retries (maxRetries=0)"],
            [1, 2, "One retry (maxRetries=1)"],
            [2, 3, "Two retries (maxRetries=2)"],
        ];

    #endregion
}
