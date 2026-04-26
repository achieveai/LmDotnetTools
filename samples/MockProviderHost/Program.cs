using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.MockProviderHost;

// CLI entry point. Boots a mock provider host on the requested port using a built-in
// "demo" scenario so the host is runnable out-of-the-box. For real test scenarios the
// host is consumed as a library — see MockProviderHost.E2E.Tests.

int port = 5099;
for (int i = 0; i < args.Length - 1; i++)
{
    if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(args[i + 1], out var parsed))
    {
        port = parsed;
        break;
    }
}

var responder = ScriptedSseResponder.New()
    .ForRole("demo", _ => true)
        .Turn(t => t.Text("Mock provider host is running."))
        .Turn(t => t.Text("Define your own scenarios in code via ScriptedSseResponder.New()."))
    .Build();

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

var app = MockProviderHostBuilder.Build(
    responder,
    urls: [$"http://127.0.0.1:{port}"],
    loggerFactory: loggerFactory);

await app.RunAsync();
