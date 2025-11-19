using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Configuration;
using AchieveAi.LmDotnetTools.AgUi.Persistence.Database;
using AchieveAi.LmDotnetTools.AgUi.Sample.Agents;
using AchieveAi.LmDotnetTools.AgUi.Sample.Tools;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ===== LOGGING CONFIGURATION =====
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Set minimum log level from configuration
var logLevel = builder.Configuration.GetValue<string>("Logging:LogLevel:Default") ?? "Information";
builder.Logging.SetMinimumLevel(Enum.Parse<LogLevel>(logLevel));

// Create early logger for startup logging
using var loggerFactory = LoggerFactory.Create(config =>
{
    config.AddConsole();
    config.SetMinimumLevel(LogLevel.Debug);
});
var startupLogger = loggerFactory.CreateLogger<Program>();

startupLogger.LogInformation("=== AG-UI Sample Application Starting ===");
startupLogger.LogInformation("Environment: {Environment}", builder.Environment.EnvironmentName);
startupLogger.LogInformation("Content root: {ContentRoot}", builder.Environment.ContentRootPath);

// ===== SERVICE CONFIGURATION =====

// Add controllers and API support
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "AG-UI Sample API",
        Version = "v1",
        Description = "Sample application demonstrating AG-UI protocol with agents and tools"
    });
});

startupLogger.LogInformation("Configuring AG-UI services...");

// Configure AG-UI with persistence
builder.Services.AddAgUi(options =>
{
    options.WebSocketPath = builder.Configuration.GetValue<string>("AgUi:WebSocketPath") ?? "/ag-ui/ws";
    options.EnablePersistence = builder.Configuration.GetValue<bool>("AgUi:EnablePersistence");
    options.DatabasePath = builder.Configuration.GetValue<string>("AgUi:DatabasePath") ?? "agui-sample.db";
    options.MaxSessionAgeHours = builder.Configuration.GetValue<int>("AgUi:MaxSessionAgeHours");
    options.EnableDebugLogging = builder.Configuration.GetValue<bool>("AgUi:EnableDebugLogging");
    options.EnableCors = true;
    options.AllowedOrigins = ImmutableList.Create("*");
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    options.MaxMessageSize = 1024 * 1024; // 1MB
    options.EventBufferSize = 1000;

    startupLogger.LogInformation("AG-UI Configuration:");
    startupLogger.LogInformation("  WebSocket Path: {Path}", options.WebSocketPath);
    startupLogger.LogInformation("  Persistence Enabled: {Enabled}", options.EnablePersistence);
    startupLogger.LogInformation("  Database Path: {DbPath}", options.DatabasePath);
    startupLogger.LogInformation("  Max Session Age: {Hours} hours", options.MaxSessionAgeHours);
    startupLogger.LogInformation("  Debug Logging: {Enabled}", options.EnableDebugLogging);
});

// Register sample agents
startupLogger.LogInformation("Registering sample agents...");
builder.Services.AddSingleton<ToolCallingAgent>();
builder.Services.AddSingleton<InstructionChainAgent>();
startupLogger.LogInformation("  Registered: ToolCallingAgent, InstructionChainAgent");

// Register sample tools as IFunctionProvider
startupLogger.LogInformation("Registering sample tools...");
builder.Services.AddSingleton<IFunctionProvider, GetWeatherTool>();
builder.Services.AddSingleton<IFunctionProvider, CalculatorTool>();
builder.Services.AddSingleton<IFunctionProvider, SearchTool>();
builder.Services.AddSingleton<IFunctionProvider, TimeTool>();
builder.Services.AddSingleton<IFunctionProvider, CounterTool>();
startupLogger.LogInformation("  Registered: GetWeatherTool, CalculatorTool, SearchTool, TimeTool, CounterTool");

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Add health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// ===== MIDDLEWARE PIPELINE =====

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== Configuring Middleware Pipeline ===");

// Configure Swagger in development
if (app.Environment.IsDevelopment())
{
    logger.LogInformation("Enabling Swagger UI (Development environment)");
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "AG-UI Sample API v1");
        c.RoutePrefix = "swagger";
    });
}

// Static files (for HTML test client)
logger.LogInformation("Enabling static file serving from wwwroot");
app.UseStaticFiles();

// CORS (must be before AG-UI middleware)
logger.LogInformation("Enabling CORS");
app.UseCors();

// AG-UI middleware (includes WebSocket configuration)
logger.LogInformation("Enabling AG-UI middleware");
app.UseAgUi();

// Map controllers
logger.LogInformation("Mapping API controllers");
app.MapControllers();

// Map health check endpoint
logger.LogInformation("Mapping health check endpoint");
app.MapHealthChecks("/health");

// Initialize database if persistence is enabled
var agUiOptions = app.Services.GetRequiredService<IOptions<AgUiOptions>>().Value;
if (agUiOptions.EnablePersistence)
{
    logger.LogInformation("Initializing database...");
    var dbInitializer = app.Services.GetService<DatabaseInitializer>();
    if (dbInitializer != null)
    {
        await dbInitializer.InitializeAsync();
        logger.LogInformation("Database initialized successfully at: {DbPath}", agUiOptions.DatabasePath);
    }
    else
    {
        logger.LogWarning("DatabaseInitializer not found - persistence may not work correctly");
    }
}

// ===== APPLICATION START =====

logger.LogInformation("=== AG-UI Sample Application Started ===");
logger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);
logger.LogInformation("Application URLs:");

foreach (var url in app.Urls)
{
    logger.LogInformation("  - {Url}", url);
}

var wsScheme = app.Urls.Any(u => u.StartsWith("https")) ? "wss" : "ws";
var httpUrl = app.Urls.FirstOrDefault() ?? "http://localhost:5000";
var host = new Uri(httpUrl).Authority;
var wsUrl = $"{wsScheme}://{host}{agUiOptions.WebSocketPath}";

logger.LogInformation("WebSocket Endpoint: {WebSocketUrl}", wsUrl);
logger.LogInformation("API Endpoint: {HttpUrl}/api/agent/run", httpUrl);
logger.LogInformation("Swagger UI: {HttpUrl}/swagger", httpUrl);
logger.LogInformation("Test Client: {HttpUrl}/test-client.html", httpUrl);
logger.LogInformation("");
logger.LogInformation("Available Agents:");
logger.LogInformation("  - ToolCallingAgent: Calls tools based on message content");
logger.LogInformation("  - InstructionChainAgent: Multi-turn instruction chain testing using TestSseMessageHandler");
logger.LogInformation("");
logger.LogInformation("Available Tools:");
logger.LogInformation("  - get_weather: Get weather for a city");
logger.LogInformation("  - calculate: Perform math operations");
logger.LogInformation("  - search: Search for information");
logger.LogInformation("  - get_current_time: Get current time");
logger.LogInformation("  - counter: Manage counters");
logger.LogInformation("");
logger.LogInformation("Ready to accept requests!");
logger.LogInformation("=========================================");

app.Run();
