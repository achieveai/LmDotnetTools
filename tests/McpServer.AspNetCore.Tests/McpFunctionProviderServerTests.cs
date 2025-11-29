using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore;
using FluentAssertions;
using McpServer.AspNetCore.Sample.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace McpServer.AspNetCore.Tests;

/// <summary>
/// Integration tests for McpFunctionProviderServer.
/// Tests server lifecycle, dynamic port allocation, tool discovery, and tool execution.
/// </summary>
public class McpFunctionProviderServerTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private McpFunctionProviderServer? _server;
    private McpClient? _client;

    public McpFunctionProviderServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string ExtractTextContent(CallToolResult result)
    {
        if (result.Content == null || result.Content.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine,
            result.Content
                .Where(c => c is TextContentBlock)
                .Cast<TextContentBlock>()
                .Select(tb => tb.Text));
    }

    public async Task InitializeAsync()
    {
        // Create server with sample tools
        _server = McpFunctionProviderServer.Create(
            new IFunctionProvider[] {
                new WeatherTool(),
                new CalculatorTool(),
                new FileInfoTool()
            },
            configureLogging: logging =>
            {
                // Enable Information logging to debug tool execution
                logging.AddConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });

        // Start the server
        await _server.StartAsync();

        _output.WriteLine($"MCP Server started on port: {_server.Port}");
        _output.WriteLine($"MCP Endpoint: {_server.McpEndpointUrl}");

        // Create MCP client using HTTP transport (renamed from SSE in ModelContextProtocol 0.4.0)
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(_server.McpEndpointUrl!),
            Name = "test-client"
        };

        var transport = new HttpClientTransport(transportOptions);
        _client = await McpClient.CreateAsync(transport);

        _output.WriteLine("MCP Client connected successfully");
    }

    public async Task DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
        }

        if (_server != null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Server_Should_Start_With_Dynamic_Port()
    {
        // Assert
        _server.Should().NotBeNull();
        _server!.Port.Should().BeGreaterThan(0, "server should be assigned a dynamic port");
        _server.BaseUrl.Should().NotBeNullOrEmpty();
        _server.McpEndpointUrl.Should().NotBeNullOrEmpty();

        _output.WriteLine($"✓ Server started on dynamic port: {_server.Port}");
    }

    [Fact]
    public async Task ListTools_Should_Return_All_Registered_Tools()
    {
        // Act
        var tools = await _client!.ListToolsAsync();

        // Assert
        tools.Should().NotBeNull();
        tools.Should().NotBeEmpty();
        tools.Should().HaveCount(4, "we registered 4 tools: get_weather, Calculator-add, Calculator-multiply, get_file_info");

        // Verify tool names
        var toolNames = tools.Select(t => t.Name).ToList();
        toolNames.Should().Contain("get_weather");
        toolNames.Should().Contain("Calculator-add");
        toolNames.Should().Contain("Calculator-multiply");
        toolNames.Should().Contain("get_file_info");

        _output.WriteLine($"✓ ListTools returned {tools.Count} tools:");
        foreach (var tool in tools)
        {
            _output.WriteLine($"  - {tool.Name}: {tool.Description}");
        }
    }

    [Fact]
    public async Task CallTool_GetWeather_Should_Return_Weather_Data()
    {
        // Arrange
        var arguments = new Dictionary<string, object>
        {
            ["city"] = "London",
            ["unit"] = "celsius"
        };

        // Act
        var result = await _client!.CallToolAsync("get_weather", arguments);

        // Assert
        result.Should().NotBeNull();
        result.Content.Should().NotBeNullOrEmpty();
        result.IsError.Should().BeFalse();

        var content = ExtractTextContent(result);
        content.Should().Contain("London", "response should include the city name");
        content.Should().Contain("celsius", "response should include the temperature unit");

        _output.WriteLine($"✓ get_weather response: {content}");
    }

    [Fact]
    public async Task CallTool_CalculatorAdd_Should_Return_Sum()
    {
        // Arrange
        var arguments = new Dictionary<string, object>
        {
            ["a"] = 15.5,
            ["b"] = 24.5
        };

        // Act
        var result = await _client!.CallToolAsync("Calculator-add", arguments);

        // Assert
        result.Should().NotBeNull();
        result.Content.Should().NotBeNullOrEmpty();

        var content = ExtractTextContent(result);
        _output.WriteLine($"Calculator-add result - IsError: {result.IsError}, Content: {content}");

        result.IsError.Should().BeFalse();
        content.Should().Contain("40", "15.5 + 24.5 = 40");

        _output.WriteLine($"✓ Calculator-add response: {content}");
    }

    [Fact]
    public async Task CallTool_CalculatorMultiply_Should_Return_Product()
    {
        // Arrange
        var arguments = new Dictionary<string, object>
        {
            ["a"] = 6.0,
            ["b"] = 7.0
        };

        // Act
        var result = await _client!.CallToolAsync("Calculator-multiply", arguments);

        // Assert
        result.Should().NotBeNull();
        result.Content.Should().NotBeNullOrEmpty();
        result.IsError.Should().BeFalse();

        var content = ExtractTextContent(result);
        content.Should().Contain("42", "6 * 7 = 42");

        _output.WriteLine($"✓ Calculator-multiply response: {content}");
    }

    [Fact]
    public async Task CallTool_GetFileInfo_Should_Return_File_Information()
    {
        // Arrange
        var testFilePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(testFilePath, "Test content");

            var arguments = new Dictionary<string, object>
            {
                ["path"] = testFilePath
            };

            // Act
            var result = await _client!.CallToolAsync("get_file_info", arguments);

            // Assert
            result.Should().NotBeNull();
            result.Content.Should().NotBeNullOrEmpty();
            result.IsError.Should().BeFalse();

            var content = ExtractTextContent(result);
            content.Should().Contain("size", "response should include file size");

            _output.WriteLine($"✓ get_file_info response: {content}");
        }
        finally
        {
            if (File.Exists(testFilePath))
            {
                File.Delete(testFilePath);
            }
        }
    }

    [Fact]
    public async Task CallTool_WithInvalidTool_Should_Return_Error()
    {
        // Arrange
        var arguments = new Dictionary<string, object>();

        // Act
        var result = await _client!.CallToolAsync("non_existent_tool", arguments);

        // Assert
        result.Should().NotBeNull();
        result.IsError.Should().BeTrue("calling a non-existent tool should return an error");
        var content = ExtractTextContent(result);
        content.Should().Contain("not found", "error message should indicate the tool was not found");

        _output.WriteLine($"✓ Non-existent tool call returned error: {content}");
    }

    [Fact]
    public async Task Server_Should_Dispose_Cleanly()
    {
        // Arrange
        var server = McpFunctionProviderServer.Create(new[] { new WeatherTool() });
        await server.StartAsync();
        var port = server.Port;

        port.Should().BeGreaterThan(0, "server should have been assigned a port");

        // Verify port is in use while server is running
        IsPortAvailable(port.Value).Should().BeFalse("port should be in use while server is running");

        // Act
        await server.DisposeAsync();

        // Assert - verify port is released after disposal
        IsPortAvailable(port.Value).Should().BeTrue("port should be available after disposal");

        _output.WriteLine($"✓ Server on port {port} disposed cleanly and port was released");
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Loopback,
                port);
            listener.Start();
            listener.Stop();
            return true; // Port is available
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false; // Port is in use
        }
    }

    [Fact]
    public async Task Multiple_Servers_Should_Get_Different_Ports()
    {
        // Arrange & Act
        var server1 = McpFunctionProviderServer.Create(new[] { new WeatherTool() });
        await server1.StartAsync();

        var server2 = McpFunctionProviderServer.Create(new[] { new CalculatorTool() });
        await server2.StartAsync();

        try
        {
            // Assert
            server1.Port.Should().BeGreaterThan(0);
            server2.Port.Should().BeGreaterThan(0);
            server1.Port.Should().NotBe(server2.Port, "each server should get a unique port");

            _output.WriteLine($"✓ Server 1 port: {server1.Port}, Server 2 port: {server2.Port}");
        }
        finally
        {
            await server1.DisposeAsync();
            await server2.DisposeAsync();
        }
    }
}
