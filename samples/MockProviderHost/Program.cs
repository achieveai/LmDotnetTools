using AchieveAi.LmDotnetTools.MockProviderHost;

// CLI entry point. Boots a mock provider host on the requested port using a JSON scenario file.
// The scenario name (or path) is taken from --scenario / LM_MOCK_SCENARIO; defaults to the
// built-in "demo" scenario shipped as an embedded resource.

int port = 5099;
string? scenario = Environment.GetEnvironmentVariable("LM_MOCK_SCENARIO");

for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase)
        && i + 1 < args.Length
        && int.TryParse(args[i + 1], out var parsedPort))
    {
        port = parsedPort;
        i++;
    }
    else if (string.Equals(args[i], "--scenario", StringComparison.OrdinalIgnoreCase)
        && i + 1 < args.Length)
    {
        scenario = args[i + 1];
        i++;
    }
}

scenario = string.IsNullOrWhiteSpace(scenario) ? "demo" : scenario;

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
var bootLogger = loggerFactory.CreateLogger("MockProviderHost");
bootLogger.LogInformation("Loading scenario '{Scenario}'", scenario);

var responder = JsonScenarioLoader.Load(scenario);

var app = MockProviderHostBuilder.Build(
    responder,
    urls: [$"http://127.0.0.1:{port}"],
    loggerFactory: loggerFactory);

await app.RunAsync();
