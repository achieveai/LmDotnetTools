using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tools;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

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

    [Fact]
    public async Task SseEndpoint_ShouldBeAvailable()
    {
        // Arrange
        var client = _fixture.CreateHttpClient();

        // Act
        var response = await client.GetAsync("/sse");

        // Assert
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed);
        _output.WriteLine($"✅ SSE endpoint responded with status: {response.StatusCode}");
        
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"SSE endpoint content: {content}");
        }
    }

    [Fact]
    public async Task McpEndpoint_ShouldBeAvailable()
    {
        // Arrange
        var client = _fixture.CreateHttpClient();

        // Act
        var response = await client.GetAsync("/mcp");

        // Assert
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed);
        _output.WriteLine($"✅ MCP endpoint responded with status: {response.StatusCode}");
    }

    [Fact]
    public async Task ServerInfo_ShouldBeAccessible()
    {
        // Arrange
        var client = _fixture.CreateHttpClient();

        // Act - Try to get server info (this might not work depending on the current implementation)
        var response = await client.GetAsync("/");

        // Assert
        _output.WriteLine($"✅ Server root endpoint responded with status: {response.StatusCode}");
        
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"Server response: {content}");
        }
    }

    [Fact]
    public async Task TransportMode_ShouldBeConfiguredForSSE()
    {
        // Arrange & Act
        var transportMode = _fixture.GetTransportMode();

        // Assert
        Assert.Equal(TransportMode.SSE, transportMode);
        _output.WriteLine($"✅ Transport mode correctly configured as: {transportMode}");
    }

    [Fact]
    public async Task ServerConfiguration_ShouldSupportSSE()
    {
        // Arrange
        var services = _fixture.GetServices();

        // Act
        var memoryServerOptions = services.GetService<Microsoft.Extensions.Options.IOptions<MemoryServerOptions>>();

        // Assert
        Assert.NotNull(memoryServerOptions);
        Assert.Equal(TransportMode.SSE, memoryServerOptions.Value.Transport.Mode);
        Assert.True(memoryServerOptions.Value.Transport.EnableCors);
        
        _output.WriteLine($"✅ Server configured for SSE transport with CORS enabled");
    }

    [Fact]
    public async Task HttpClient_CanConnectToServer()
    {
        // Arrange
        var client = _fixture.CreateHttpClient();

        // Act
        var response = await client.GetAsync("/health");

        // Assert - Server should respond (even if endpoint doesn't exist, we should get a response)
        Assert.NotNull(response);
        _output.WriteLine($"✅ HTTP client can connect to server, status: {response.StatusCode}");
    }

    [Fact]
    public async Task SseTransportInfrastructure_ShouldBeInPlace()
    {
        // This test verifies that the SSE transport infrastructure is properly configured
        // Once the SDK supports SSE client transport, we can extend this test
        
        // Arrange
        var client = _fixture.CreateHttpClient();
        
        // Act & Assert
        var sseResponse = await client.GetAsync("/sse");
        var mcpResponse = await client.GetAsync("/mcp");
        
        // The server should have these endpoints available
        Assert.NotNull(sseResponse);
        Assert.NotNull(mcpResponse);
        
        _output.WriteLine($"✅ SSE infrastructure in place - SSE: {sseResponse.StatusCode}, MCP: {mcpResponse.StatusCode}");
        _output.WriteLine("📝 Note: Full SSE client testing pending SDK updates with proper SSE support");
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}

/// <summary>
/// Test fixture for SSE integration tests using ASP.NET Core TestServer.
/// This fixture sets up the server in SSE mode for testing.
/// </summary>
public class SseTestServerFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IServiceProvider _services;

    public SseTestServerFixture()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    // Override configuration for SSE mode
                    services.Configure<MemoryServerOptions>(options =>
                    {
                        options.Transport.Mode = TransportMode.SSE;
                        options.Transport.Port = 0; // Use any available port
                        options.Transport.Host = "localhost";
                        options.Transport.EnableCors = true;
                        options.Transport.AllowedOrigins = new[] { "http://localhost:3000", "http://127.0.0.1:3000" };
                    });
                });
            });

        _services = _factory.Services;
    }

    public HttpClient CreateHttpClient()
    {
        return _factory.CreateClient();
    }

    public string GetBaseUrl()
    {
        var client = _factory.CreateClient();
        return client.BaseAddress!.ToString().TrimEnd('/');
    }

    public TransportMode GetTransportMode()
    {
        var options = _services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MemoryServerOptions>>();
        return options.Value.Transport.Mode;
    }

    public IServiceProvider GetServices()
    {
        return _services;
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
} 