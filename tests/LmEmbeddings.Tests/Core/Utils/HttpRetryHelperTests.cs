using AchieveAi.LmDotnetTools.LmCore.Http;
using LmEmbeddings.Tests.TestUtilities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit;

namespace LmEmbeddings.Tests.Core.Utils;

/// <summary>
/// Comprehensive tests for HttpRetryHelper utility class
/// Tests retry logic, error detection, and exponential backoff behavior
/// </summary>
public class HttpRetryHelperTests
{
    private readonly ILogger<HttpRetryHelperTests> _logger;

    public HttpRetryHelperTests()
    {
        _logger = new TestLogger<HttpRetryHelperTests>();
    }

    [Theory]
    [MemberData(nameof(ExecuteWithRetryAsyncTestCases))]
    public async Task ExecuteWithRetryAsync_WithVariousScenarios_BehavesCorrectly(
        int failureCount,
        bool shouldSucceed,
        string expectedResult,
        string description)
    {
        Debug.WriteLine($"Testing ExecuteWithRetryAsync: {description}");
        Debug.WriteLine($"Failure count: {failureCount}, Expected to succeed: {shouldSucceed}");

        // Arrange
        var attemptCount = 0;
        var operation = new Func<Task<string>>(() =>
        {
            attemptCount++;
            Debug.WriteLine($"Operation attempt {attemptCount}");
            
            if (attemptCount <= failureCount)
            {
                throw new HttpRequestException("Simulated network timeout");
            }
            return Task.FromResult("Success");
        });

        // Act & Assert
        if (shouldSucceed)
        {
            var result = await HttpRetryHelper.ExecuteWithRetryAsync(operation, _logger, maxRetries: 3);
            Assert.Equal(expectedResult, result);
            Debug.WriteLine($"✓ Operation succeeded after {attemptCount} attempts");
        }
        else
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(
                () => HttpRetryHelper.ExecuteWithRetryAsync(operation, _logger, maxRetries: 3));
            Assert.Contains("Simulated network timeout", exception.Message);
            Debug.WriteLine($"✓ Operation failed as expected after {attemptCount} attempts");
        }
    }

    [Theory]
    [MemberData(nameof(ExecuteHttpWithRetryAsyncTestCases))]
    public async Task ExecuteHttpWithRetryAsync_WithVariousScenarios_BehavesCorrectly(
        HttpStatusCode[] statusCodes,
        bool shouldSucceed,
        string description)
    {
        Debug.WriteLine($"Testing ExecuteHttpWithRetryAsync: {description}");
        Debug.WriteLine($"Status codes: [{string.Join(", ", statusCodes)}], Expected to succeed: {shouldSucceed}");

        // Arrange
        var attemptCount = 0;
        var httpOperation = new Func<Task<HttpResponseMessage>>(() =>
        {
            var statusCode = statusCodes[Math.Min(attemptCount, statusCodes.Length - 1)];
            attemptCount++;
            Debug.WriteLine($"HTTP attempt {attemptCount}, returning status: {statusCode}");
            
            var response = new HttpResponseMessage(statusCode);
            if (statusCode == HttpStatusCode.OK)
            {
                response.Content = new StringContent("{\"result\":\"success\"}", System.Text.Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        });

        var responseProcessor = new Func<HttpResponseMessage, Task<string>>(async response =>
        {
            var content = await response.Content.ReadAsStringAsync();
            return content;
        });

        // Act & Assert
        if (shouldSucceed)
        {
            var result = await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                httpOperation, responseProcessor, _logger, maxRetries: 3);
            Assert.Contains("success", result);
            Debug.WriteLine($"✓ HTTP operation succeeded after {attemptCount} attempts");
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(
                () => HttpRetryHelper.ExecuteHttpWithRetryAsync(
                    httpOperation, responseProcessor, _logger, maxRetries: 3));
            Debug.WriteLine($"✓ HTTP operation failed as expected after {attemptCount} attempts");
        }
    }

    [Theory]
    [MemberData(nameof(IsRetryableStatusCodeTestCases))]
    public void IsRetryableStatusCode_WithVariousStatusCodes_ReturnsExpectedResult(
        HttpStatusCode statusCode,
        bool expectedResult,
        string description)
    {
        Debug.WriteLine($"Testing IsRetryableStatusCode: {description}");
        Debug.WriteLine($"Status code: {statusCode} ({(int)statusCode}), Expected retryable: {expectedResult}");

        // Act
        var result = HttpRetryHelper.IsRetryableStatusCode(statusCode);

        // Assert
        Assert.Equal(expectedResult, result);
        Debug.WriteLine($"✓ Status code {statusCode} correctly identified as {(result ? "retryable" : "non-retryable")}");
    }

    [Theory]
    [MemberData(nameof(IsRetryableErrorTestCases))]
    public void IsRetryableError_WithVariousExceptions_ReturnsExpectedResult(
        string errorMessage,
        bool expectedResult,
        string description)
    {
        Debug.WriteLine($"Testing IsRetryableError: {description}");
        Debug.WriteLine($"Error message: '{errorMessage}', Expected retryable: {expectedResult}");

        // Arrange
        var exception = new HttpRequestException(errorMessage);

        // Act
        var result = HttpRetryHelper.IsRetryableError(exception);

        // Assert
        Assert.Equal(expectedResult, result);
        Debug.WriteLine($"✓ Error message correctly identified as {(result ? "retryable" : "non-retryable")}");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_WithDisposalCheck_ThrowsObjectDisposedException()
    {
        Debug.WriteLine("Testing ExecuteWithRetryAsync with disposal check");

        // Arrange
        var operation = new Func<Task<string>>(() => Task.FromResult("Success"));
        var checkDisposed = new Func<bool>(() => true); // Always disposed

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => HttpRetryHelper.ExecuteWithRetryAsync(operation, _logger, checkDisposed: checkDisposed));
        
        Assert.Contains("Service has been disposed", exception.Message);
        Debug.WriteLine($"✓ ObjectDisposedException thrown as expected: {exception.Message}");
    }

    [Fact]
    public async Task ExecuteHttpWithRetryAsync_WithDisposalCheck_ThrowsObjectDisposedException()
    {
        Debug.WriteLine("Testing ExecuteHttpWithRetryAsync with disposal check");

        // Arrange
        var httpOperation = new Func<Task<HttpResponseMessage>>(() => 
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var responseProcessor = new Func<HttpResponseMessage, Task<string>>(response => 
            Task.FromResult("Success"));
        var checkDisposed = new Func<bool>(() => true); // Always disposed

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => HttpRetryHelper.ExecuteHttpWithRetryAsync(
                httpOperation, responseProcessor, _logger, checkDisposed: checkDisposed));
        
        Assert.Contains("Service has been disposed", exception.Message);
        Debug.WriteLine($"✓ ObjectDisposedException thrown as expected: {exception.Message}");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_WithCancellation_ThrowsOperationCancelledException()
    {
        Debug.WriteLine("Testing ExecuteWithRetryAsync with cancellation");

        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var operation = new Func<Task<string>>(() =>
        {
            cts.Token.ThrowIfCancellationRequested();
            return Task.FromResult("Success");
        });

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => HttpRetryHelper.ExecuteWithRetryAsync(operation, _logger, cancellationToken: cts.Token));
        
        Debug.WriteLine("✓ OperationCanceledException thrown as expected");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_WithMaxRetries_StopsAfterLimit()
    {
        Debug.WriteLine("Testing ExecuteWithRetryAsync respects max retries limit");

        // Arrange
        var attemptCount = 0;
        var maxRetries = 2;
        var operation = new Func<Task<string>>(() =>
        {
            attemptCount++;
            Debug.WriteLine($"Operation attempt {attemptCount}");
            throw new HttpRequestException("network timeout");
        });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => HttpRetryHelper.ExecuteWithRetryAsync(operation, _logger, maxRetries: maxRetries));
        
        // Should try: initial attempt + maxRetries = 1 + 2 = 3 total attempts
        Assert.Equal(maxRetries + 1, attemptCount);
        Debug.WriteLine($"✓ Operation attempted {attemptCount} times (initial + {maxRetries} retries)");
    }

    #region Test Data

    public static IEnumerable<object[]> ExecuteWithRetryAsyncTestCases => new List<object[]>
    {
        new object[] { 0, true, "Success", "Operation succeeds immediately" },
        new object[] { 1, true, "Success", "Operation succeeds after 1 retry" },
        new object[] { 3, true, "Success", "Operation succeeds after 3 retries (at limit)" },
        new object[] { 4, false, "", "Operation fails after exceeding retry limit" }
    };

    public static IEnumerable<object[]> ExecuteHttpWithRetryAsyncTestCases => new List<object[]>
    {
        new object[] 
        { 
            new[] { HttpStatusCode.OK }, 
            true, 
            "Immediate success with 200 OK" 
        },
        new object[] 
        { 
            new[] { HttpStatusCode.InternalServerError, HttpStatusCode.OK }, 
            true, 
            "Success after retrying 500 error" 
        },
        new object[] 
        { 
            new[] { HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK }, 
            true, 
            "Success after retrying multiple 5xx errors" 
        },
        new object[] 
        { 
            new[] { HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, 
                    HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout }, 
            false, 
            "Failure after multiple 5xx errors exceed retry limit" 
        },
        new object[] 
        { 
            new[] { HttpStatusCode.BadRequest }, 
            false, 
            "Immediate failure with 400 Bad Request (non-retryable)" 
        },
        new object[] 
        { 
            new[] { HttpStatusCode.NotFound }, 
            false, 
            "Immediate failure with 404 Not Found (non-retryable)" 
        }
    };

    public static IEnumerable<object[]> IsRetryableStatusCodeTestCases => new List<object[]>
    {
        // Retryable 5xx errors
        new object[] { HttpStatusCode.InternalServerError, true, "500 Internal Server Error should be retryable" },
        new object[] { HttpStatusCode.NotImplemented, true, "501 Not Implemented should be retryable" },
        new object[] { HttpStatusCode.BadGateway, true, "502 Bad Gateway should be retryable" },
        new object[] { HttpStatusCode.ServiceUnavailable, true, "503 Service Unavailable should be retryable" },
        new object[] { HttpStatusCode.GatewayTimeout, true, "504 Gateway Timeout should be retryable" },
        new object[] { (HttpStatusCode)599, true, "599 (custom 5xx) should be retryable" },
        
        // Non-retryable 4xx errors
        new object[] { HttpStatusCode.BadRequest, false, "400 Bad Request should not be retryable" },
        new object[] { HttpStatusCode.Unauthorized, false, "401 Unauthorized should not be retryable" },
        new object[] { HttpStatusCode.Forbidden, false, "403 Forbidden should not be retryable" },
        new object[] { HttpStatusCode.NotFound, false, "404 Not Found should not be retryable" },
        new object[] { HttpStatusCode.Conflict, false, "409 Conflict should not be retryable" },
        
        // Non-retryable 2xx/3xx
        new object[] { HttpStatusCode.OK, false, "200 OK should not be retryable" },
        new object[] { HttpStatusCode.Created, false, "201 Created should not be retryable" },
        new object[] { HttpStatusCode.Redirect, false, "302 Redirect should not be retryable" },
        new object[] { HttpStatusCode.NotModified, false, "304 Not Modified should not be retryable" }
    };

    public static IEnumerable<object[]> IsRetryableErrorTestCases => new List<object[]>
    {
        // Retryable network/timeout errors
        new object[] { "Connection timeout occurred", true, "Timeout errors should be retryable" },
        new object[] { "Network error detected", true, "Network errors should be retryable" },
        new object[] { "Request timeout", true, "Request timeout should be retryable" },
        new object[] { "Network connection failed", true, "Network connection failures should be retryable" },
        
        // Retryable 5xx status code errors
        new object[] { "Response status code does not indicate success: 500 (Internal Server Error)", true, "500 errors should be retryable" },
        new object[] { "Response status code does not indicate success: 502 (Bad Gateway)", true, "502 errors should be retryable" },
        new object[] { "Response status code does not indicate success: 503 (Service Unavailable)", true, "503 errors should be retryable" },
        new object[] { "Response status code does not indicate success: 504 (Gateway Timeout)", true, "504 errors should be retryable" },
        new object[] { "Internal Server Error occurred", true, "Internal Server Error text should be retryable" },
        new object[] { "Bad Gateway detected", true, "Bad Gateway text should be retryable" },
        new object[] { "Service Unavailable", true, "Service Unavailable text should be retryable" },
        new object[] { "Gateway Timeout", true, "Gateway Timeout text should be retryable" },
        
        // Non-retryable 4xx errors
        new object[] { "Response status code does not indicate success: 400 (Bad Request)", false, "400 errors should not be retryable" },
        new object[] { "Response status code does not indicate success: 401 (Unauthorized)", false, "401 errors should not be retryable" },
        new object[] { "Response status code does not indicate success: 404 (Not Found)", false, "404 errors should not be retryable" },
        
        // Non-retryable generic errors
        new object[] { "Invalid request format", false, "Generic errors should not be retryable" },
        new object[] { "Authentication failed", false, "Authentication errors should not be retryable" },
        new object[] { "Resource not found", false, "Resource errors should not be retryable" }
    };

    #endregion

    #region Test Implementation

    private class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Debug.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
    }

    #endregion
} 