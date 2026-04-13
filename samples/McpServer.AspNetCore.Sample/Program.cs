using AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;
using McpServer.AspNetCore.Sample.Tools;

var builder = WebApplication.CreateBuilder(args);

// Parse port from command line arguments (default: 5123)
var port = args.Contains("--port") && args.Length > Array.IndexOf(args, "--port") + 1
    ? int.Parse(args[Array.IndexOf(args, "--port") + 1])
    : 5123;

Console.WriteLine($"🚀 Starting MCP Server on http://localhost:{port}/mcp");

// Configure Kestrel to listen on specific port
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(port);
});

// Add logging
builder.Logging.AddConsole();

// Register function providers
builder.Services.AddFunctionProvider<WeatherTool>();
builder.Services.AddFunctionProvider<CalculatorTool>();
builder.Services.AddFunctionProvider<FileInfoTool>();

// Add MCP server with function provider support
builder.Services.AddMcpServerFromFunctionProviders();

// Add CORS for development/testing
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Enable CORS
app.UseCors();

// Map MCP endpoints
app.MapMcpFunctionProviders();

// Add a health check endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    message = "MCP Server is running",
    endpoint = $"http://localhost:{port}/mcp",
    mcp_config = new
    {
        command = "npx",
        args = new[] { "mcp-remote@latest", $"http://localhost:{port}/mcp" }
    }
}));

// Display startup information
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine("✅ MCP Server started successfully!");
    Console.WriteLine();
    Console.WriteLine($"📍 MCP Endpoint: http://localhost:{port}/mcp");
    Console.WriteLine($"🔍 Health Check: http://localhost:{port}/health");
    Console.WriteLine();
    Console.WriteLine("📦 Available Tools:");
    Console.WriteLine("   - get_weather: Get weather for a city");
    Console.WriteLine("   - Calculator-add: Add two numbers");
    Console.WriteLine("   - Calculator-multiply: Multiply two numbers");
    Console.WriteLine("   - get_file_info: Get file information");
    Console.WriteLine();
    Console.WriteLine("🔌 Claude Code MCP Configuration:");
    Console.WriteLine("   {");
    Console.WriteLine("     \"command\": \"npx\",");
    Console.WriteLine($"     \"args\": [\"mcp-remote@latest\", \"http://localhost:{port}/mcp\"]");
    Console.WriteLine("   }");
    Console.WriteLine();
});

await app.RunAsync();
