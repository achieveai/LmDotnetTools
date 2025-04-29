namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utils;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Data.Sqlite;
using Moq;
using Moq.Protected;
using Xunit;

public class HttpClientCacheTests : IDisposable
{
  private readonly SqliteConnection _connection;
  private readonly string _dbPath;

  public HttpClientCacheTests()
  {
    _dbPath = $"DataSource=:memory:";
    _connection = new SqliteConnection(_dbPath);
    _connection.Open();
  }

  public void Dispose()
  {
    if (_connection != null)
    {
      _connection.Close();
      _connection.Dispose();
    }
  }

  [Fact]
  public async Task GetStringAsync_CachesMisses()
  {
    // Arrange
    var handlerMock = new Mock<HttpMessageHandler>();
    var responseMessage = new HttpResponseMessage
    {
      StatusCode = HttpStatusCode.OK,
      Content = new StringContent("test response")
    };

    handlerMock
      .Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync(responseMessage);

    var httpClient = new HttpClient(handlerMock.Object);
    var cache = new HttpClientCache(httpClient, _connection);

    // Act
    string response1 = await cache.GetStringAsync("https://example.com/api");
    string response2 = await cache.GetStringAsync("https://example.com/api");

    // Assert
    Assert.Equal("test response", response1);
    Assert.Equal("test response", response2);

    // Verify HttpClient was called only once
    handlerMock.Protected().Verify(
      "SendAsync",
      Times.Once(),
      ItExpr.IsAny<HttpRequestMessage>(),
      ItExpr.IsAny<CancellationToken>());
  }

  [Fact]
  public async Task GetStringAsync_DifferentUrls_MakesDifferentRequests()
  {
    // Arrange
    var handlerMock = new Mock<HttpMessageHandler>();
    
    handlerMock
      .Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString() == "https://example.com/api1"),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync(new HttpResponseMessage
      {
        StatusCode = HttpStatusCode.OK,
        Content = new StringContent("response 1")
      });
      
    handlerMock
      .Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString() == "https://example.com/api2"),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync(new HttpResponseMessage
      {
        StatusCode = HttpStatusCode.OK,
        Content = new StringContent("response 2")
      });

    var httpClient = new HttpClient(handlerMock.Object);
    var cache = new HttpClientCache(httpClient, _connection);

    // Act
    string response1 = await cache.GetStringAsync("https://example.com/api1");
    string response2 = await cache.GetStringAsync("https://example.com/api2");

    // Assert
    Assert.Equal("response 1", response1);
    Assert.Equal("response 2", response2);
  }

  [Fact]
  public async Task GetSseAsync_CachesAndReplaysEvents()
  {
    // Arrange
    var handlerMock = new Mock<HttpMessageHandler>();
    var content = new StringContent(
      "data: event1\ndata: event2\ndata: event3\n",
      Encoding.UTF8,
      "text/event-stream");
      
    var responseMessage = new HttpResponseMessage
    {
      StatusCode = HttpStatusCode.OK,
      Content = content
    };

    handlerMock
      .Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync(responseMessage);

    var httpClient = new HttpClient(handlerMock.Object);
    var cache = new HttpClientCache(httpClient, _connection);

    // Act - first request
    var events1 = new List<string>();
    await foreach (var e in cache.GetSseAsync("https://example.com/sse"))
    {
      events1.Add(e);
    }

    // Act - second request (should be from cache)
    var events2 = new List<string>();
    await foreach (var e in cache.GetSseAsync("https://example.com/sse"))
    {
      events2.Add(e);
    }

    // Assert
    Assert.Equal(new[] { "event1", "event2", "event3" }, events1);
    Assert.Equal(new[] { "event1", "event2", "event3" }, events2);

    // Verify HttpClient was called only once
    handlerMock.Protected().Verify(
      "SendAsync",
      Times.Once(),
      ItExpr.IsAny<HttpRequestMessage>(),
      ItExpr.IsAny<CancellationToken>());
  }

  [Fact]
  public async Task GetStringAsync_ThrowsForSseUrl()
  {
    // Arrange
    var handlerMock = new Mock<HttpMessageHandler>();
    var content = new StringContent(
      "data: event1\ndata: event2\n",
      Encoding.UTF8,
      "text/event-stream");
      
    var responseMessage = new HttpResponseMessage
    {
      StatusCode = HttpStatusCode.OK,
      Content = content
    };

    handlerMock
      .Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync(responseMessage);

    var httpClient = new HttpClient(handlerMock.Object);
    var cache = new HttpClientCache(httpClient, _connection);

    // First cache it as SSE
    await foreach (var _ in cache.GetSseAsync("https://example.com/sse")) { }

    // Act & Assert - trying to get as string should throw
    await Assert.ThrowsAsync<InvalidOperationException>(
      () => cache.GetStringAsync("https://example.com/sse"));
  }

  [Fact]
  public async Task GetSseAsync_ThrowsForNonSseUrl()
  {
    // Arrange
    var handlerMock = new Mock<HttpMessageHandler>();
    var responseMessage = new HttpResponseMessage
    {
      StatusCode = HttpStatusCode.OK,
      Content = new StringContent("regular response")
    };

    handlerMock
      .Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync(responseMessage);

    var httpClient = new HttpClient(handlerMock.Object);
    var cache = new HttpClientCache(httpClient, _connection);

    // First cache it as regular response
    await cache.GetStringAsync("https://example.com/api");

    // Act & Assert - trying to get as SSE should throw
    await Assert.ThrowsAsync<InvalidOperationException>(async () => 
    {
      await foreach (var _ in cache.GetSseAsync("https://example.com/api")) { }
    });
  }

  [Fact]
  public async Task MultithreadedAccess_SameUrl_ThreadSafe()
  {
    // Arrange
    int requestCount = 0;
    var handlerMock = new Mock<HttpMessageHandler>();
    
    handlerMock
      .Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) => 
      {
        Interlocked.Increment(ref requestCount);
        int currentCount = requestCount;
        return new HttpResponseMessage
        {
          StatusCode = HttpStatusCode.OK,
          Content = new StringContent($"response {currentCount}")
        };
      });

    var httpClient = new HttpClient(handlerMock.Object);
    var cache = new HttpClientCache(httpClient, _connection);

    // Act - run 10 parallel requests for the same URL
    var tasks = new List<Task<string>>();
    for (int i = 0; i < 10; i++)
    {
      tasks.Add(cache.GetStringAsync("https://example.com/api"));
    }

    var results = await Task.WhenAll(tasks);

    // Assert
    // Only one request should have been made
    Assert.Equal(1, requestCount);
    
    // All results should be the same
    Assert.All(results, r => Assert.Equal(results[0], r));
  }

  [Fact]
  public async Task MultithreadedAccess_DifferentUrls_ParallelRequests()
  {
    // Arrange
    int requestCount = 0;
    var handlerMock = new Mock<HttpMessageHandler>();
    
    handlerMock
      .Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) => 
      {
        Interlocked.Increment(ref requestCount);
        // Add a small delay to simulate network latency and increase chance of race conditions
        Thread.Sleep(10);
        return new HttpResponseMessage
        {
          StatusCode = HttpStatusCode.OK,
          Content = new StringContent($"response for {req.RequestUri}")
        };
      });

    var httpClient = new HttpClient(handlerMock.Object);
    var cache = new HttpClientCache(httpClient, _connection);

    // Act - run parallel requests for different URLs
    var tasks = new List<Task<string>>();
    for (int i = 0; i < 5; i++)
    {
      string url = $"https://example.com/api{i}";
      tasks.Add(cache.GetStringAsync(url));
    }

    var results = await Task.WhenAll(tasks);

    // Assert
    // All 5 requests should have been made
    Assert.Equal(5, requestCount);
    
    // Results should be different for each URL
    for (int i = 0; i < 5; i++)
    {
      Assert.Contains($"response for https://example.com/api{i}", results);
    }
  }
}
