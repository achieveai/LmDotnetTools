using ModelContextProtocol.Client;
using Xunit.Abstractions;

namespace MemoryServer.Tests.Integrations;

/// <summary>
/// STDIO transport implementation of the MCP transport test suite.
/// This class provides STDIO-specific client creation and server management
/// while inheriting all the core MCP functionality tests from the base class.
/// </summary>
public class StdioMcpTransportTests : McpTransportTestBase
{
    private readonly string _serverExecutablePath;
    private static readonly string[] options = ["--stdio"];

    public StdioMcpTransportTests(ITestOutputHelper output)
        : base(output)
    {
        // Path to the Memory MCP Server executable
        var assemblyLocation = Path.GetDirectoryName(typeof(StdioMcpTransportTests).Assembly.Location)!;
        _serverExecutablePath = Path.Combine(assemblyLocation, "MemoryServer.exe");

        // If not found in test output, try the actual build location
        if (!File.Exists(_serverExecutablePath))
        {
            _serverExecutablePath = Path.Combine(
                assemblyLocation,
                "..",
                "..",
                "..",
                "McpServers",
                "Memory",
                "MemoryServer",
                "bin",
                "Debug",
                "net9.0",
                "MemoryServer.exe"
            );
        }
    }

    protected override string GetTransportName()
    {
        return "STDIO";
    }

    protected override async Task<IMcpClient> CreateClientAsync()
    {
        _output.WriteLine($"🔌 Creating STDIO MCP client transport: {_serverExecutablePath}");

        if (!File.Exists(_serverExecutablePath))
        {
            throw new FileNotFoundException($"Server executable not found at: {_serverExecutablePath}");
        }

        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "memory-server",
                Command = _serverExecutablePath,
                Arguments = options,
            }
        );

        var client = await McpClientFactory.CreateAsync(transport);
        _output.WriteLine("✅ STDIO MCP client connected successfully");

        return client;
    }
}
