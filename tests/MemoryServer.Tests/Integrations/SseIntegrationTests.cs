using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit.Abstractions;
using Microsoft.AspNetCore.TestHost;
using System.ComponentModel;

namespace MemoryServer.Tests.Integrations;

/// <summary>
/// Integration tests for the Memory MCP Server using SSE transport.
/// These tests verify that the SSE infrastructure is in place and working.
/// 
/// NOTE: Full SSE client testing is pending SDK updates with proper SSE support.
/// Currently testing the server-side SSE endpoint availability and basic functionality.
/// </summary>
public class SseIntegrationTests : IClassFixture<SseTestServerFixture>, IDisposable
{
    private readonly SseTestServerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SseIntegrationTests(SseTestServerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact(Timeout = 15000)]
    public async Task SseEndpoint_ShouldBeAvailable()
    {
        // Arrange
        var client = _fixture.CreateHttpClient();

        // Act - Check if SSE endpoint exists using HEAD request (don't try to consume as regular HTTP)
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/sse"));

        // Assert - SSE endpoint should exist (even if it returns method not allowed for HEAD)
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK ||
                   response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed ||
                   response.StatusCode == System.Net.HttpStatusCode.BadRequest);
        _output.WriteLine($"✅ SSE endpoint exists and responds: {response.StatusCode}");
    }

    [Fact(Timeout = 15000)]
    public async Task HealthEndpoint_ShouldWork()
    {
        // Arrange
        var client = _fixture.CreateHttpClient();

        // Act
        var response = await client.GetAsync("/health");

        // Assert
        Assert.True(response.IsSuccessStatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("OK", content);
        _output.WriteLine($"✅ Health endpoint works: {response.StatusCode}");
    }

    [Fact(Timeout = 15000)]
    public async Task SseTransportInfrastructure_ShouldBeInPlace()
    {
        // This test verifies that the SSE transport infrastructure is properly configured
        // Note: SSE endpoints are designed for persistent connections, not simple HTTP requests

        // Arrange
        var client = _fixture.CreateHttpClient();

        // Act & Assert - Check that SSE endpoint exists using HEAD request
        var sseResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/sse"));

        // Log actual status codes for debugging
        _output.WriteLine($"SSE endpoint status: {sseResponse.StatusCode}");

        // The server should have the SSE endpoint available
        Assert.NotNull(sseResponse);

        // Check if endpoint exists (not returning NotFound)
        var sseExists = sseResponse.StatusCode != System.Net.HttpStatusCode.NotFound;
        Assert.True(sseExists, $"SSE endpoint should exist. Status: {sseResponse.StatusCode}");

        _output.WriteLine($"✅ SSE infrastructure in place - SSE: {sseResponse.StatusCode}");
        _output.WriteLine("📝 Note: SSE endpoints are designed for persistent connections, not simple HTTP requests");
    }

    [Fact]
    public void MemoryServerServices_ShouldBeRegistered()
    {
        // Arrange
        var services = _fixture.GetServices();

        // Act & Assert - Check that actual MemoryServer services are available
        Assert.NotNull(services);

        // Verify core MemoryServer services are registered (not just basic MCP)
        var memoryService = services.GetService<MemoryServer.Services.IMemoryService>();
        var sessionResolver = services.GetService<MemoryServer.Services.ISessionContextResolver>();
        var memoryTools = services.GetService<MemoryServer.Tools.MemoryMcpTools>();

        Assert.NotNull(memoryService);
        Assert.NotNull(sessionResolver);
        Assert.NotNull(memoryTools);

        _output.WriteLine($"✅ MemoryServer services properly registered in SSE transport");
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}

/// <summary>
/// Test fixture for SSE integration tests using the actual MemoryServer Startup class.
/// This fixture uses the same configuration as production MemoryServer to ensure
/// we're testing the real implementation, not a minimal mock.
/// </summary>
public class SseTestServerFixture : IDisposable
{
    private readonly TestServer _server;
    private readonly IServiceProvider _services;

    public SseTestServerFixture()
    {
        var builder = new WebHostBuilder()
            .UseEnvironment("Testing")
            .UseStartup<Startup>(); // Use MemoryServer's actual Startup class

        _server = new TestServer(builder);
        _services = _server.Services;
    }

    public HttpClient CreateHttpClient()
    {
        return _server.CreateClient();
    }

    public IServiceProvider GetServices()
    {
        return _services;
    }

    public void Dispose()
    {
        _server?.Dispose();
    }
}

/// <summary>
/// Simple test tool for minimal SSE testing
/// </summary>
[McpServerToolType]
public class SimpleTestTool
{
    [McpServerTool, Description("Simple test tool that returns a greeting")]
    public static string SayHello(string name) => $"Hello, {name}!";
}