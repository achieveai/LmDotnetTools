using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Configuration;
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Middleware;

/// <summary>
/// ASP.NET Core middleware that handles AG-UI WebSocket connections
/// </summary>
public sealed class AgUiMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AgUiOptions _options;
    private readonly ILogger<AgUiMiddleware> _logger;

    public AgUiMiddleware(
        RequestDelegate next,
        IOptions<AgUiOptions> options,
        ILogger<AgUiMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Processes the HTTP request and handles WebSocket upgrade if path matches
    /// </summary>
    public async Task InvokeAsync(HttpContext context, AgUiWebSocketHandler webSocketHandler)
    {
        // Check if request path matches AG-UI endpoint
        if (!context.Request.Path.StartsWithSegments(_options.WebSocketPath, StringComparison.OrdinalIgnoreCase))
        {
            // Not an AG-UI request, pass to next middleware
            await _next(context);
            return;
        }

        _logger.LogDebug("AG-UI endpoint hit: {Path}", context.Request.Path);

        // Validate that this is a WebSocket request
        if (!context.WebSockets.IsWebSocketRequest)
        {
            _logger.LogWarning("Non-WebSocket request to AG-UI endpoint: {Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection required");
            return;
        }

        // Validate origin if CORS is enabled
        if (_options.EnableCors && !IsOriginAllowed(context))
        {
            _logger.LogWarning("Origin not allowed: {Origin}", context.Request.Headers.Origin.ToString());
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Origin not allowed");
            return;
        }

        // Handle the WebSocket connection
        _logger.LogInformation("Handling WebSocket connection for AG-UI");
        await webSocketHandler.HandleWebSocketAsync(context, context.RequestAborted);
    }

    /// <summary>
    /// Checks if the request origin is allowed based on CORS configuration
    /// </summary>
    private bool IsOriginAllowed(HttpContext context)
    {
        if (_options.AllowedOrigins.Count == 0)
        {
            // No restrictions
            return true;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // No origin header - allow for same-origin requests
            return true;
        }

        // Check if origin matches any allowed origins
        return _options.AllowedOrigins.Any(allowed =>
            allowed.Equals("*", StringComparison.Ordinal) ||
            allowed.Equals(origin, StringComparison.OrdinalIgnoreCase));
    }
}
