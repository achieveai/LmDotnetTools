using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tools;

/// <summary>
/// Bridges LmDotnetTools <see cref="FunctionContract"/> registrations to the Copilot
/// ACP dynamic tool surface. Copilot sends <c>tools/call</c> (or equivalent) requests
/// for tools declared in the <c>session/new</c> payload; this bridge routes them to the
/// registered handlers and normalises responses into ACP <c>contentItems</c>.
/// </summary>
public sealed class CopilotDynamicToolBridge
{
    private readonly IReadOnlyDictionary<string, FunctionContract> _contractsByName;
    private readonly IReadOnlyDictionary<string, Func<string, Task<string>>> _handlersByName;
    private readonly CopilotToolPolicyEngine _toolPolicy;
    private readonly ILogger<CopilotDynamicToolBridge>? _logger;
    private readonly JsonSerializerOptions _json;

    public CopilotDynamicToolBridge(
        IEnumerable<FunctionContract> contracts,
        IDictionary<string, Func<string, Task<string>>> handlers,
        CopilotToolPolicyEngine toolPolicy,
        ILogger<CopilotDynamicToolBridge>? logger = null)
    {
        _contractsByName = (contracts ?? throw new ArgumentNullException(nameof(contracts)))
            .Where(static c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.Last(), StringComparer.OrdinalIgnoreCase);

        _handlersByName = new Dictionary<string, Func<string, Task<string>>>(
            handlers ?? throw new ArgumentNullException(nameof(handlers)),
            StringComparer.OrdinalIgnoreCase);
        _toolPolicy = toolPolicy ?? throw new ArgumentNullException(nameof(toolPolicy));
        _logger = logger;
        _json = JsonSerializerOptionsFactory.CreateForProduction();
    }

    public IReadOnlyList<CopilotDynamicToolSpec> GetToolSpecs()
    {
        var specs = new List<CopilotDynamicToolSpec>();
        foreach (var (toolName, contract) in _contractsByName)
        {
            if (!_toolPolicy.IsDynamicToolAllowed(toolName))
            {
                continue;
            }

            var schema = contract.GetJsonSchema() ?? JsonSchemaObject.Create().AllowAdditionalProperties(true).Build();
            var schemaElement = JsonSerializer.SerializeToElement(schema, _json);
            specs.Add(
                new CopilotDynamicToolSpec
                {
                    Name = toolName,
                    Description = contract.Description,
                    InputSchema = schemaElement,
                });
        }

        return specs;
    }

    public async Task<CopilotDynamicToolCallResponse> ExecuteAsync(
        CopilotDynamicToolCallRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = DateTimeOffset.UtcNow;

        if (!_toolPolicy.IsDynamicToolAllowed(request.Tool))
        {
            _logger?.LogInformation(
                "{event_type} {event_status} {tool_name} {latency_ms}",
                "copilot.dynamic_tool.execution",
                "denied",
                request.Tool,
                (DateTimeOffset.UtcNow - start).TotalMilliseconds);
            return Failure($"Tool '{request.Tool}' is not enabled for this session.");
        }

        if (!_handlersByName.TryGetValue(request.Tool, out var handler))
        {
            _logger?.LogInformation(
                "{event_type} {event_status} {tool_name} {latency_ms}",
                "copilot.dynamic_tool.execution",
                "failed",
                request.Tool,
                (DateTimeOffset.UtcNow - start).TotalMilliseconds);
            return Failure($"Tool '{request.Tool}' is not registered.");
        }

        var argsJson = request.Arguments.ValueKind == JsonValueKind.Undefined ? "{}" : request.Arguments.GetRawText();
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await handler(argsJson);
            _logger?.LogInformation(
                "{event_type} {event_status} {tool_name} {latency_ms}",
                "copilot.dynamic_tool.execution",
                "completed",
                request.Tool,
                (DateTimeOffset.UtcNow - start).TotalMilliseconds);
            return Success(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "{event_type} {event_status} {tool_name}",
                "copilot.dynamic_tool.execution",
                "failed",
                request.Tool);
            return Failure(ex.Message);
        }
    }

    private static CopilotDynamicToolCallResponse Success(string text)
    {
        return new CopilotDynamicToolCallResponse
        {
            Success = true,
            ContentItems =
            [
                new CopilotDynamicToolContentItem
                {
                    Type = "text",
                    Text = text,
                },
            ],
        };
    }

    private static CopilotDynamicToolCallResponse Failure(string message)
    {
        return new CopilotDynamicToolCallResponse
        {
            Success = false,
            ContentItems =
            [
                new CopilotDynamicToolContentItem
                {
                    Type = "text",
                    Text = message,
                },
            ],
        };
    }
}
