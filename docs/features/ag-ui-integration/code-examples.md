# AG-UI Code Examples

This document provides comprehensive implementation examples and usage patterns for the AG-UI integration.

## Table of Contents
1. [Basic Setup](#basic-setup)
2. [Complete Sample Application](#complete-sample-application)
3. [Custom Agent Implementation](#custom-agent-implementation)
4. [Client-Side Integration](#client-side-integration)
5. [Advanced Scenarios](#advanced-scenarios)
6. [Testing Examples](#testing-examples)

## Basic Setup

### Minimal ASP.NET Core Application

```csharp
// Program.cs
using AchieveAi.LmDotnetTools.AgUi.AspNetCore;
using AchieveAi.LmDotnetTools.AgUi.Extensions;
using AchieveAi.LmDotnetTools.LmCore;

var builder = WebApplication.CreateBuilder(args);

// Add AG-UI services
builder.Services.AddAgUi(options =>
{
    options.EndpointPath = "/ag-ui/ws";
    options.EnableDetailedLogging = true;
});

// Register your agent
builder.Services.AddSingleton<IStreamingAgent>(sp =>
{
    // Create and configure your agent
    var agent = new MyCustomAgent();

    // Add AG-UI support
    return agent.WithAgUiSupport(sp);
});

var app = builder.Build();

// Enable AG-UI middleware
app.UseAgUi();

// Map AG-UI WebSocket endpoint
app.MapAgUi();

app.Run();
```

### With SQLite Persistence

```csharp
// Program.cs with persistence
var builder = WebApplication.CreateBuilder(args);

// Add AG-UI with SQLite
var connectionString = builder.Configuration.GetConnectionString("AgUiDatabase")
    ?? "Data Source=agui.db;Cache=Shared";

builder.Services.AddAgUiWithSqlite(connectionString, options =>
{
    options.EnablePersistence = true;
    options.SessionTimeout = TimeSpan.FromHours(2);
});

// Register agent with persistence support
builder.Services.AddSingleton<IStreamingAgent>(sp =>
{
    var agent = new MyCustomAgent();
    var sessionManager = sp.GetRequiredService<ISessionManager>();

    // Configure agent with session recovery
    return agent
        .WithAgUiSupport(sp)
        .WithSessionRecovery(sessionManager);
});

var app = builder.Build();

// Run database migrations
await app.MigrateAgUiDatabaseAsync();

app.UseAgUi();
app.MapAgUi();

app.Run();
```

## Complete Sample Application

### Project Structure

```
AgUiSampleApp/
├── Program.cs                 # Application entry point
├── Agents/
│   ├── ChatAgent.cs          # Custom chat agent
│   └── ToolAgent.cs          # Agent with tool support
├── Services/
│   ├── WeatherService.cs     # Sample tool service
│   └── DatabaseService.cs    # Sample database tool
├── Middleware/
│   └── CustomMiddleware.cs   # Custom middleware
├── wwwroot/
│   ├── index.html            # Test client
│   ├── app.js               # Client JavaScript
│   └── styles.css           # Client styles
└── appsettings.json         # Configuration
```

### Main Application (Program.cs)

```csharp
using AchieveAi.LmDotnetTools.AgUi.AspNetCore;
using AchieveAi.LmDotnetTools.LmCore;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AgUiSampleApp.Agents;
using AgUiSampleApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Add services
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<DatabaseService>();

// Configure AG-UI
builder.Services.AddAgUiWithSqlite("Data Source=sample.db", options =>
{
    options.EndpointPath = "/ag-ui/ws";
    options.EnablePersistence = true;
    options.EnableDetailedLogging = true;
    options.SessionTimeout = TimeSpan.FromHours(1);

    // Custom event handlers
    options.Handlers.OnSessionStarted = async context =>
    {
        Console.WriteLine($"Session started: {context.SessionId}");
    };

    options.Handlers.OnError = async context =>
    {
        Console.WriteLine($"Error in session {context.SessionId}: {context.Error.Message}");
    };
});

// Configure function registry
builder.Services.AddSingleton<FunctionRegistry>(sp =>
{
    var registry = new FunctionRegistry();
    var weatherService = sp.GetRequiredService<WeatherService>();
    var dbService = sp.GetRequiredService<DatabaseService>();

    // Register tools
    registry.AddFunctionsFromObject(weatherService);
    registry.AddFunctionsFromObject(dbService);

    return registry;
});

// Configure agent
builder.Services.AddSingleton<IStreamingAgent>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ToolAgent>>();
    var functionRegistry = sp.GetRequiredService<FunctionRegistry>();

    var agent = new ToolAgent(logger);

    // Build the correct middleware pipeline
    return agent
        .WithMiddleware(new JsonFragmentUpdateMiddleware())
        .WithMiddleware(sp.GetRequiredService<AgUiStreamingMiddleware>())
        .WithMiddleware(functionRegistry.BuildMiddleware())
        .WithMiddleware(new ConsolePrinterMiddleware())
        .WithMiddleware(new MessageUpdateJoinerMiddleware());
});

// Add static files for test client
builder.Services.AddStaticFiles();

var app = builder.Build();

// Run migrations
await app.MigrateAgUiDatabaseAsync();

// Middleware pipeline
app.UseStaticFiles();
app.UseAgUi();

// Map endpoints
app.MapAgUi("/ag-ui/ws");
app.MapFallbackToFile("index.html");

Console.WriteLine("AG-UI Sample Application started");
Console.WriteLine("Open http://localhost:5000 in your browser");

app.Run();
```

### Configuration (appsettings.json)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "AchieveAi.LmDotnetTools": "Debug"
    }
  },
  "AgUi": {
    "EndpointPath": "/ag-ui/ws",
    "RequireAuthentication": false,
    "MaxMessageSize": 131072,
    "KeepAliveInterval": "00:00:30",
    "SessionTimeout": "01:00:00",
    "EnablePersistence": true,
    "EnableDetailedLogging": true,
    "AllowedOrigins": ["http://localhost:3000", "http://localhost:5000"],
    "RateLimit": {
      "Enabled": true,
      "RequestsPerMinute": 120,
      "MaxConcurrentConnections": 20
    }
  },
  "ConnectionStrings": {
    "AgUiDatabase": "Data Source=sample.db;Cache=Shared"
  },
  "LlmProvider": {
    "Model": "gpt-4",
    "Temperature": 0.7,
    "MaxTokens": 2048
  }
}
```

## Custom Agent Implementation

### Tool-Enabled Agent

```csharp
// Agents/ToolAgent.cs
using AchieveAi.LmDotnetTools.LmCore;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using System.Runtime.CompilerServices;

namespace AgUiSampleApp.Agents;

public class ToolAgent : IStreamingAgent
{
    private readonly ILogger<ToolAgent> _logger;
    private readonly HttpClient _httpClient;

    public ToolAgent(ILogger<ToolAgent> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
    }

    public async IAsyncEnumerable<IMessage> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastUserMessage = messages
            .OfType<TextMessage>()
            .LastOrDefault(m => m.Role == MessageRole.User);

        if (lastUserMessage == null)
        {
            yield return new ErrorMessage
            {
                Code = "NO_USER_MESSAGE",
                Message = "No user message found"
            };
            yield break;
        }

        // Start text message
        var messageId = Guid.NewGuid().ToString();
        yield return new TextMessageUpdate
        {
            Id = messageId,
            IsStart = true
        };

        // Check if user is asking about weather
        if (lastUserMessage.Content.Contains("weather", StringComparison.OrdinalIgnoreCase))
        {
            // Start tool call
            var toolCallId = Guid.NewGuid().ToString();
            yield return new ToolCallUpdate
            {
                Id = toolCallId,
                FunctionName = "get_weather",
                IsStart = true
            };

            // Stream tool arguments
            var city = ExtractCity(lastUserMessage.Content) ?? "New York";
            var arguments = $"{{\"city\":\"{city}\"}}";

            for (int i = 0; i < arguments.Length; i += 10)
            {
                var chunk = arguments.Substring(i, Math.Min(10, arguments.Length - i));
                yield return new ToolCallUpdate
                {
                    Id = toolCallId,
                    ArgumentsJson = chunk,
                    ArgumentsComplete = i + 10 >= arguments.Length
                };

                await Task.Delay(50, ct); // Simulate streaming
            }

            // Simulate tool execution
            await Task.Delay(500, ct);

            // Return tool result
            yield return new ToolCallResultUpdate
            {
                ToolCallId = toolCallId,
                Result = new { temperature = 72, condition = "Sunny" },
                Success = true
            };

            // Complete tool call
            yield return new ToolCallUpdate
            {
                Id = toolCallId,
                IsComplete = true
            };

            // Stream response based on tool result
            var response = $"The weather in {city} is currently 72°F and sunny.";
            foreach (var chunk in ChunkText(response, 5))
            {
                yield return new TextMessageUpdate
                {
                    Id = messageId,
                    Content = chunk
                };

                await Task.Delay(30, ct); // Simulate typing
            }
        }
        else
        {
            // Regular response without tools
            var response = "I can help you check the weather. Just ask me about the weather in any city!";
            foreach (var chunk in ChunkText(response, 5))
            {
                yield return new TextMessageUpdate
                {
                    Id = messageId,
                    Content = chunk
                };

                await Task.Delay(30, ct);
            }
        }

        // Complete text message
        yield return new TextMessageUpdate
        {
            Id = messageId,
            IsComplete = true
        };
    }

    private string? ExtractCity(string text)
    {
        // Simple city extraction logic
        var keywords = new[] { "in ", "for ", "at " };
        foreach (var keyword in keywords)
        {
            var index = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var start = index + keyword.Length;
                var end = text.IndexOfAny(new[] { '.', '?', '!' }, start);
                if (end < 0) end = text.Length;

                return text.Substring(start, end - start).Trim();
            }
        }
        return null;
    }

    private IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
        }
    }
}
```

### Tool Services

```csharp
// Services/WeatherService.cs
using AchieveAi.LmDotnetTools.LmCore.Attributes;

namespace AgUiSampleApp.Services;

public class WeatherService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WeatherService> _logger;

    public WeatherService(ILogger<WeatherService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
    }

    [Function("get_weather", "Get current weather for a city")]
    public async Task<WeatherResult> GetWeatherAsync(
        [Parameter("city", "The city name", required: true)] string city,
        [Parameter("units", "Temperature units (celsius/fahrenheit)", required: false)] string units = "fahrenheit")
    {
        _logger.LogInformation("Getting weather for {City} in {Units}", city, units);

        // Simulate API call
        await Task.Delay(Random.Shared.Next(100, 500));

        return new WeatherResult
        {
            City = city,
            Temperature = Random.Shared.Next(60, 90),
            Condition = RandomCondition(),
            Humidity = Random.Shared.Next(30, 80),
            Units = units
        };
    }

    [Function("get_forecast", "Get weather forecast for a city")]
    public async Task<ForecastResult> GetForecastAsync(
        [Parameter("city", "The city name", required: true)] string city,
        [Parameter("days", "Number of days", required: false)] int days = 5)
    {
        _logger.LogInformation("Getting {Days}-day forecast for {City}", days, city);

        await Task.Delay(Random.Shared.Next(200, 600));

        var forecast = new List<DayForecast>();
        for (int i = 0; i < days; i++)
        {
            forecast.Add(new DayForecast
            {
                Date = DateTime.Today.AddDays(i),
                High = Random.Shared.Next(70, 95),
                Low = Random.Shared.Next(50, 70),
                Condition = RandomCondition()
            });
        }

        return new ForecastResult
        {
            City = city,
            Days = forecast
        };
    }

    private string RandomCondition()
    {
        var conditions = new[] { "Sunny", "Cloudy", "Partly Cloudy", "Rainy", "Windy" };
        return conditions[Random.Shared.Next(conditions.Length)];
    }
}

public class WeatherResult
{
    public string City { get; set; }
    public int Temperature { get; set; }
    public string Condition { get; set; }
    public int Humidity { get; set; }
    public string Units { get; set; }
}

public class ForecastResult
{
    public string City { get; set; }
    public List<DayForecast> Days { get; set; }
}

public class DayForecast
{
    public DateTime Date { get; set; }
    public int High { get; set; }
    public int Low { get; set; }
    public string Condition { get; set; }
}
```

## Client-Side Integration

### HTML Test Client

```html
<!-- wwwroot/index.html -->
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>AG-UI Test Client</title>
    <link rel="stylesheet" href="styles.css">
</head>
<body>
    <div class="container">
        <header>
            <h1>AG-UI Test Client</h1>
            <div class="status" id="status">Disconnected</div>
        </header>

        <div class="chat-container">
            <div class="messages" id="messages"></div>

            <div class="input-container">
                <input type="text" id="messageInput" placeholder="Type a message..." disabled>
                <button id="sendButton" disabled>Send</button>
            </div>
        </div>

        <div class="tools-panel">
            <h3>Active Tool Calls</h3>
            <div id="toolCalls"></div>
        </div>
    </div>

    <script src="app.js"></script>
</body>
</html>
```

### JavaScript Client

```javascript
// wwwroot/app.js
class AgUiClient {
    constructor(url) {
        this.url = url;
        this.ws = null;
        this.sessionId = null;
        this.messageHandlers = new Map();
        this.currentMessage = null;
        this.activeTool calls = new Map();
    }

    connect() {
        return new Promise((resolve, reject) => {
            this.ws = new WebSocket(this.url);

            this.ws.onopen = () => {
                console.log('Connected to AG-UI');
                this.updateStatus('Connected');
                resolve();
            };

            this.ws.onmessage = (event) => {
                const data = JSON.parse(event.data);
                this.handleEvent(data);
            };

            this.ws.onerror = (error) => {
                console.error('WebSocket error:', error);
                this.updateStatus('Error');
                reject(error);
            };

            this.ws.onclose = () => {
                console.log('Disconnected from AG-UI');
                this.updateStatus('Disconnected');
                this.reconnect();
            };
        });
    }

    handleEvent(event) {
        console.log('Event:', event.type, event);

        switch (event.type) {
            case 'session-started':
                this.sessionId = event.sessionId;
                this.addSystemMessage(`Session started: ${this.sessionId}`);
                break;

            case 'text-message-start':
                this.currentMessage = {
                    id: event.messageId,
                    role: event.role,
                    content: '',
                    element: this.createMessageElement(event.role)
                };
                break;

            case 'text-message-content':
                if (this.currentMessage && this.currentMessage.id === event.messageId) {
                    this.currentMessage.content += event.content;
                    this.updateMessageContent(this.currentMessage.element, this.currentMessage.content);
                }
                break;

            case 'text-message-end':
                if (this.currentMessage && this.currentMessage.id === event.messageId) {
                    this.currentMessage = null;
                }
                break;

            case 'tool-call-start':
                this.activeToolCalls.set(event.toolCallId, {
                    name: event.toolName,
                    arguments: '',
                    element: this.createToolCallElement(event.toolName)
                });
                break;

            case 'tool-call-arguments':
                const toolCall = this.activeToolCalls.get(event.toolCallId);
                if (toolCall) {
                    toolCall.arguments += event.argumentsChunk;
                    this.updateToolCallArguments(toolCall.element, toolCall.arguments);
                }
                break;

            case 'tool-call-result':
                const call = this.activeToolCalls.get(event.toolCallId);
                if (call) {
                    this.updateToolCallResult(call.element, event.result);
                }
                break;

            case 'tool-call-end':
                setTimeout(() => {
                    const tc = this.activeToolCalls.get(event.toolCallId);
                    if (tc && tc.element) {
                        tc.element.classList.add('completed');
                    }
                    this.activeToolCalls.delete(event.toolCallId);
                }, 2000);
                break;

            case 'error':
                this.addErrorMessage(`Error: ${event.message}`);
                break;

            case 'run-finished':
                this.addSystemMessage('Agent processing complete');
                this.enableInput();
                break;
        }
    }

    sendMessage(message) {
        if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
            console.error('WebSocket not connected');
            return;
        }

        this.disableInput();
        this.addUserMessage(message);

        const request = {
            type: 'run-agent',
            payload: {
                message: message,
                configuration: {
                    temperature: 0.7,
                    maxTokens: 2048
                }
            }
        };

        this.ws.send(JSON.stringify(request));
    }

    createMessageElement(role) {
        const element = document.createElement('div');
        element.className = `message ${role}`;
        document.getElementById('messages').appendChild(element);
        return element;
    }

    updateMessageContent(element, content) {
        element.textContent = content;
        element.scrollIntoView({ behavior: 'smooth', block: 'end' });
    }

    createToolCallElement(toolName) {
        const element = document.createElement('div');
        element.className = 'tool-call active';
        element.innerHTML = `
            <div class="tool-name">${toolName}</div>
            <div class="tool-arguments"></div>
            <div class="tool-result"></div>
        `;
        document.getElementById('toolCalls').appendChild(element);
        return element;
    }

    updateToolCallArguments(element, args) {
        const argsElement = element.querySelector('.tool-arguments');
        argsElement.textContent = `Arguments: ${args}`;
    }

    updateToolCallResult(element, result) {
        const resultElement = element.querySelector('.tool-result');
        resultElement.textContent = `Result: ${JSON.stringify(result, null, 2)}`;
    }

    addUserMessage(text) {
        const element = this.createMessageElement('user');
        element.textContent = text;
    }

    addSystemMessage(text) {
        const element = this.createMessageElement('system');
        element.textContent = text;
    }

    addErrorMessage(text) {
        const element = this.createMessageElement('error');
        element.textContent = text;
    }

    updateStatus(status) {
        const statusElement = document.getElementById('status');
        statusElement.textContent = status;
        statusElement.className = `status ${status.toLowerCase()}`;
    }

    enableInput() {
        document.getElementById('messageInput').disabled = false;
        document.getElementById('sendButton').disabled = false;
    }

    disableInput() {
        document.getElementById('messageInput').disabled = true;
        document.getElementById('sendButton').disabled = true;
    }

    reconnect() {
        setTimeout(() => {
            console.log('Attempting to reconnect...');
            this.connect();
        }, 3000);
    }
}

// Initialize client
const client = new AgUiClient(`ws://${window.location.host}/ag-ui/ws`);

// Connect on page load
window.addEventListener('DOMContentLoaded', async () => {
    try {
        await client.connect();
        client.enableInput();
    } catch (error) {
        console.error('Failed to connect:', error);
    }
});

// Handle send button
document.getElementById('sendButton').addEventListener('click', () => {
    const input = document.getElementById('messageInput');
    const message = input.value.trim();

    if (message) {
        client.sendMessage(message);
        input.value = '';
    }
});

// Handle enter key
document.getElementById('messageInput').addEventListener('keypress', (e) => {
    if (e.key === 'Enter') {
        document.getElementById('sendButton').click();
    }
});
```

### Styles

```css
/* wwwroot/styles.css */
* {
    margin: 0;
    padding: 0;
    box-sizing: border-box;
}

body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
    background: #f5f5f5;
    height: 100vh;
}

.container {
    display: flex;
    flex-direction: column;
    height: 100%;
    max-width: 1200px;
    margin: 0 auto;
}

header {
    background: white;
    padding: 1rem;
    display: flex;
    justify-content: space-between;
    align-items: center;
    border-bottom: 1px solid #ddd;
}

h1 {
    font-size: 1.5rem;
    color: #333;
}

.status {
    padding: 0.5rem 1rem;
    border-radius: 20px;
    font-size: 0.875rem;
    font-weight: 500;
}

.status.connected {
    background: #4caf50;
    color: white;
}

.status.disconnected {
    background: #f44336;
    color: white;
}

.status.error {
    background: #ff9800;
    color: white;
}

.chat-container {
    flex: 1;
    display: flex;
    flex-direction: column;
    background: white;
    margin: 1rem;
    border-radius: 8px;
    box-shadow: 0 2px 4px rgba(0,0,0,0.1);
}

.messages {
    flex: 1;
    padding: 1rem;
    overflow-y: auto;
}

.message {
    margin-bottom: 1rem;
    padding: 0.75rem;
    border-radius: 8px;
}

.message.user {
    background: #007bff;
    color: white;
    margin-left: auto;
    max-width: 70%;
}

.message.assistant {
    background: #f1f3f4;
    color: #333;
    max-width: 70%;
}

.message.system {
    background: #fff3cd;
    color: #856404;
    text-align: center;
    font-size: 0.875rem;
}

.message.error {
    background: #f8d7da;
    color: #721c24;
}

.input-container {
    display: flex;
    padding: 1rem;
    border-top: 1px solid #ddd;
}

#messageInput {
    flex: 1;
    padding: 0.75rem;
    border: 1px solid #ddd;
    border-radius: 4px;
    font-size: 1rem;
}

#sendButton {
    margin-left: 0.5rem;
    padding: 0.75rem 1.5rem;
    background: #007bff;
    color: white;
    border: none;
    border-radius: 4px;
    cursor: pointer;
    font-size: 1rem;
}

#sendButton:disabled {
    background: #ccc;
    cursor: not-allowed;
}

.tools-panel {
    background: white;
    margin: 0 1rem 1rem;
    padding: 1rem;
    border-radius: 8px;
    box-shadow: 0 2px 4px rgba(0,0,0,0.1);
    max-height: 200px;
    overflow-y: auto;
}

.tool-call {
    background: #f9f9f9;
    padding: 0.75rem;
    margin-bottom: 0.5rem;
    border-radius: 4px;
    border-left: 3px solid #007bff;
}

.tool-call.active {
    animation: pulse 1s infinite;
}

.tool-call.completed {
    border-left-color: #4caf50;
    opacity: 0.7;
}

.tool-name {
    font-weight: bold;
    color: #333;
    margin-bottom: 0.25rem;
}

.tool-arguments,
.tool-result {
    font-size: 0.875rem;
    color: #666;
    font-family: monospace;
    white-space: pre-wrap;
}

@keyframes pulse {
    0% { opacity: 1; }
    50% { opacity: 0.7; }
    100% { opacity: 1; }
}
```

## Advanced Scenarios

### Custom Middleware

```csharp
// Middleware/RateLimitingMiddleware.cs
public class RateLimitingMiddleware : IStreamingMiddleware
{
    private readonly Dictionary<string, RateLimiter> _limiters = new();
    private readonly int _requestsPerMinute;

    public RateLimitingMiddleware(int requestsPerMinute = 60)
    {
        _requestsPerMinute = requestsPerMinute;
    }

    public async IAsyncEnumerable<IMessage> InvokeAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        GenerateReplyOptions options,
        IAsyncEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var userId = context.Properties.TryGetValue("UserId", out var uid)
            ? uid?.ToString() ?? "anonymous"
            : "anonymous";

        var limiter = GetOrCreateLimiter(userId);

        if (!limiter.AllowRequest())
        {
            yield return new ErrorMessage
            {
                Code = "RATE_LIMIT_EXCEEDED",
                Message = $"Rate limit of {_requestsPerMinute} requests per minute exceeded"
            };
            yield break;
        }

        await foreach (var message in messages.WithCancellation(ct))
        {
            yield return message;
        }
    }

    private RateLimiter GetOrCreateLimiter(string userId)
    {
        if (!_limiters.TryGetValue(userId, out var limiter))
        {
            limiter = new RateLimiter(_requestsPerMinute);
            _limiters[userId] = limiter;
        }
        return limiter;
    }

    private class RateLimiter
    {
        private readonly int _maxRequests;
        private readonly Queue<DateTime> _requestTimes = new();

        public RateLimiter(int maxRequests)
        {
            _maxRequests = maxRequests;
        }

        public bool AllowRequest()
        {
            var now = DateTime.UtcNow;
            var windowStart = now.AddMinutes(-1);

            // Remove old requests outside the window
            while (_requestTimes.Count > 0 && _requestTimes.Peek() < windowStart)
            {
                _requestTimes.Dequeue();
            }

            if (_requestTimes.Count >= _maxRequests)
            {
                return false;
            }

            _requestTimes.Enqueue(now);
            return true;
        }
    }
}
```

### Authentication Integration

```csharp
// Program.cs with JWT authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]))
        };

        // Support WebSocket authentication
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/ag-ui/ws"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAgUi(options =>
{
    options.RequireAuthentication = true;
    options.AuthenticationScheme = JwtBearerDefaults.AuthenticationScheme;

    options.Handlers.OnBeforeRequest = async context =>
    {
        // Validate user permissions
        var user = context.HttpContext.User;
        if (!user.HasClaim("permission", "use-ai"))
        {
            context.HttpContext.Response.StatusCode = 403;
            await context.HttpContext.Response.WriteAsync("Insufficient permissions");
            return false;
        }
        return true;
    };
});
```

## Testing Examples

### Unit Tests

```csharp
// Tests/AgUiProtocolHandlerTests.cs
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;

public class AgUiProtocolHandlerTests
{
    [Fact]
    public async Task ProcessRequest_ValidInput_ReturnsEvents()
    {
        // Arrange
        var mockAgent = new Mock<IStreamingAgent>();
        var mockLogger = new Mock<ILogger<AgUiProtocolHandler>>();

        var messages = CreateTestMessageStream();
        mockAgent.Setup(a => a.GenerateReplyStreamingAsync(
            It.IsAny<IEnumerable<IMessage>>(),
            It.IsAny<GenerateReplyOptions>(),
            It.IsAny<CancellationToken>()))
            .Returns(messages);

        var handler = new AgUiProtocolHandler(mockAgent.Object, mockLogger.Object);

        var request = new RunAgentInput
        {
            Message = "Hello, world!",
            Configuration = new RunConfiguration
            {
                Temperature = 0.7,
                MaxTokens = 100
            }
        };

        // Act
        var events = new List<AgUiEventBase>();
        await foreach (var evt in handler.ProcessRequestAsync(request))
        {
            events.Add(evt);
        }

        // Assert
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e is RunStartedEvent);
        Assert.Contains(events, e => e is TextMessageStartEvent);
        Assert.Contains(events, e => e is TextMessageContentEvent);
        Assert.Contains(events, e => e is RunFinishedEvent);
    }

    private async IAsyncEnumerable<IMessage> CreateTestMessageStream()
    {
        yield return new TextMessageUpdate { Id = "1", IsStart = true };
        yield return new TextMessageUpdate { Id = "1", Content = "Hello" };
        yield return new TextMessageUpdate { Id = "1", Content = " from test!" };
        yield return new TextMessageUpdate { Id = "1", IsComplete = true };
    }
}
```

### Integration Tests

```csharp
// Tests/WebSocketIntegrationTests.cs
public class WebSocketIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebSocketIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task WebSocket_Connect_ReceivesSessionStarted()
    {
        // Arrange
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Override services for testing
                services.AddSingleton<IStreamingAgent, TestAgent>();
            });
        }).CreateClient();

        var wsClient = _factory.Server.CreateWebSocketClient();

        // Act
        var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/ag-ui/ws"),
            CancellationToken.None);

        var buffer = new ArraySegment<byte>(new byte[4096]);
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);

        // Assert
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);

        var json = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
        var evt = JsonSerializer.Deserialize<AgUiEventBase>(json);

        Assert.Equal("session-started", evt.Type);
        Assert.NotNull(evt.SessionId);
    }
}
```

### Load Testing

```csharp
// Tests/LoadTests.cs
using NBomber.CSharp;
using NBomber.WebSockets;

public class LoadTests
{
    [Fact]
    public void WebSocket_LoadTest()
    {
        var scenario = Scenario.Create("ag_ui_load_test", async context =>
        {
            var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri("ws://localhost:5000/ag-ui/ws"), context.CancellationToken);

            // Send message
            var request = JsonSerializer.Serialize(new
            {
                type = "run-agent",
                payload = new
                {
                    message = "What's the weather like?"
                }
            });

            await ws.SendAsync(
                Encoding.UTF8.GetBytes(request),
                WebSocketMessageType.Text,
                true,
                context.CancellationToken);

            // Receive events until complete
            var buffer = new ArraySegment<byte>(new byte[4096]);
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, context.CancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
                var evt = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (evt["type"].ToString() == "run-finished")
                {
                    break;
                }
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", context.CancellationToken);
            return Response.Ok();
        })
        .WithLoadSimulations(
            Simulation.InjectPerSec(rate: 10, during: TimeSpan.FromSeconds(30))
        );

        NBomberRunner
            .RegisterScenarios(scenario)
            .Run();
    }
}
```

## Deployment Examples

### Docker Deployment

```dockerfile
# Dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["AgUiSampleApp.csproj", "."]
RUN dotnet restore "AgUiSampleApp.csproj"
COPY . .
RUN dotnet build "AgUiSampleApp.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AgUiSampleApp.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create directory for SQLite database
RUN mkdir -p /app/data

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV AgUi__EnablePersistence=true
ENV ConnectionStrings__AgUiDatabase="Data Source=/app/data/agui.db;Cache=Shared"

ENTRYPOINT ["dotnet", "AgUiSampleApp.dll"]
```

### Kubernetes Deployment

```yaml
# kubernetes/deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: agui-app
spec:
  replicas: 3
  selector:
    matchLabels:
      app: agui-app
  template:
    metadata:
      labels:
        app: agui-app
    spec:
      containers:
      - name: agui-app
        image: agui-sample:latest
        ports:
        - containerPort: 80
        env:
        - name: AgUi__EnablePersistence
          value: "true"
        - name: ConnectionStrings__AgUiDatabase
          value: "Data Source=/data/agui.db;Cache=Shared"
        volumeMounts:
        - name: data
          mountPath: /data
      volumes:
      - name: data
        persistentVolumeClaim:
          claimName: agui-data-pvc

---
apiVersion: v1
kind: Service
metadata:
  name: agui-service
spec:
  selector:
    app: agui-app
  ports:
  - port: 80
    targetPort: 80
  type: LoadBalancer
```

## References

- [AG-UI Protocol Documentation](https://docs.ag-ui.com)
- [CopilotKit Documentation](https://docs.copilotkit.ai)
- [LmCore Documentation](../../LmCore/README.md)
- [ASP.NET Core Documentation](https://docs.microsoft.com/aspnet/core)