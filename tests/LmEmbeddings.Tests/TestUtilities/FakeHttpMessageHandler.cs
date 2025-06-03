using System.Net;
using System.Text;

namespace LmEmbeddings.Tests.TestUtilities;

/// <summary>
/// Fake HTTP message handler for testing HTTP requests without making real network calls
/// Based on the pattern described in mocking-httpclient.md
/// </summary>
public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handlerFunc;

    public FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc)
    {
        _handlerFunc = handlerFunc ?? throw new ArgumentNullException(nameof(handlerFunc));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return _handlerFunc(request, cancellationToken);
    }

    /// <summary>
    /// Creates a simple fake handler with a custom response function
    /// </summary>
    /// <param name="responseFunc">Function to generate responses</param>
    /// <returns>A configured fake handler</returns>
    public static FakeHttpMessageHandler CreateSimpleHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFunc)
    {
        return new FakeHttpMessageHandler((request, cancellationToken) =>
        {
            return Task.FromResult(responseFunc(request));
        });
    }

    /// <summary>
    /// Creates a simple fake handler that returns a successful response with JSON content
    /// </summary>
    /// <param name="jsonResponse">The JSON response to return</param>
    /// <param name="statusCode">The HTTP status code to return</param>
    /// <returns>A configured fake handler</returns>
    public static FakeHttpMessageHandler CreateSimpleJsonHandler(
        string jsonResponse, 
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new FakeHttpMessageHandler((request, cancellationToken) =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });
    }

    /// <summary>
    /// Creates a fake handler that can respond to multiple different requests
    /// </summary>
    /// <param name="responses">Dictionary mapping request patterns to responses</param>
    /// <returns>A configured fake handler</returns>
    public static FakeHttpMessageHandler CreateMultiResponseHandler(
        Dictionary<string, (string json, HttpStatusCode status)> responses)
    {
        return new FakeHttpMessageHandler((request, cancellationToken) =>
        {
            var key = $"{request.Method} {request.RequestUri?.PathAndQuery}";
            
            if (responses.TryGetValue(key, out var response))
            {
                var httpResponse = new HttpResponseMessage(response.status)
                {
                    Content = new StringContent(response.json, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(httpResponse);
            }

            // Default: return 404
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Not Found", Encoding.UTF8, "text/plain")
            });
        });
    }

    /// <summary>
    /// Creates a fake handler that simulates network errors or timeouts
    /// </summary>
    /// <param name="exception">The exception to throw</param>
    /// <returns>A configured fake handler that throws exceptions</returns>
    public static FakeHttpMessageHandler CreateErrorHandler(Exception exception)
    {
        return new FakeHttpMessageHandler((request, cancellationToken) =>
        {
            throw exception;
        });
    }

    /// <summary>
    /// Creates a fake handler that simulates retry scenarios
    /// </summary>
    /// <param name="failureCount">Number of times to fail before succeeding</param>
    /// <param name="successResponse">The successful response to return after failures</param>
    /// <param name="failureStatus">The HTTP status to return for failures</param>
    /// <returns>A configured fake handler for retry testing</returns>
    public static FakeHttpMessageHandler CreateRetryHandler(
        int failureCount,
        string successResponse,
        HttpStatusCode failureStatus = HttpStatusCode.InternalServerError)
    {
        var attemptCount = 0;
        
        return new FakeHttpMessageHandler((request, cancellationToken) =>
        {
            attemptCount++;
            
            if (attemptCount <= failureCount)
            {
                return Task.FromResult(new HttpResponseMessage(failureStatus)
                {
                    Content = new StringContent($"Failure attempt {attemptCount}", Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successResponse, Encoding.UTF8, "application/json")
            });
        });
    }

    /// <summary>
    /// Creates a fake handler that returns a sequence of status codes followed by success
    /// </summary>
    /// <param name="statusCodes">Sequence of status codes to return</param>
    /// <param name="successResponse">The successful response to return after all status codes</param>
    /// <returns>A configured fake handler for status code sequence testing</returns>
    public static FakeHttpMessageHandler CreateStatusCodeSequenceHandler(
        HttpStatusCode[] statusCodes,
        string successResponse)
    {
        var attemptCount = 0;
        
        return new FakeHttpMessageHandler((request, cancellationToken) =>
        {
            if (attemptCount < statusCodes.Length)
            {
                var statusCode = statusCodes[attemptCount];
                attemptCount++;
                
                if (statusCode == HttpStatusCode.OK)
                {
                    return Task.FromResult(new HttpResponseMessage(statusCode)
                    {
                        Content = new StringContent(successResponse, Encoding.UTF8, "application/json")
                    });
                }
                
                return Task.FromResult(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent($"Error {(int)statusCode}", Encoding.UTF8, "text/plain")
                });
            }

            // If we've exhausted the sequence, return success
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successResponse, Encoding.UTF8, "application/json")
            });
        });
    }
} 