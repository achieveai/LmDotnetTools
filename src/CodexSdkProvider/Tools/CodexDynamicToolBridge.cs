using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tools;

public sealed class CodexDynamicToolBridge
{
    private readonly IReadOnlyDictionary<string, FunctionContract> _contractsByName;
    private readonly IReadOnlyDictionary<string, Func<string, Task<string>>> _handlersByName;
    private readonly CodexToolPolicyEngine _toolPolicy;
    private readonly ILogger<CodexDynamicToolBridge>? _logger;
    private readonly JsonSerializerOptions _json;

    public CodexDynamicToolBridge(
        IEnumerable<FunctionContract> contracts,
        IDictionary<string, Func<string, Task<string>>> handlers,
        CodexToolPolicyEngine toolPolicy,
        ILogger<CodexDynamicToolBridge>? logger = null)
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

    public IReadOnlyList<CodexDynamicToolSpec> GetToolSpecs()
    {
        var specs = new List<CodexDynamicToolSpec>();
        foreach (var (toolName, contract) in _contractsByName)
        {
            if (!_toolPolicy.IsDynamicToolAllowed(toolName))
            {
                continue;
            }

            var schema = contract.GetJsonSchema() ?? JsonSchemaObject.Create().AllowAdditionalProperties(true).Build();
            var schemaElement = JsonSerializer.SerializeToElement(schema, _json);
            specs.Add(
                new CodexDynamicToolSpec
                {
                    Name = toolName,
                    Description = contract.Description,
                    InputSchema = schemaElement,
                });
        }

        return specs;
    }

    public async Task<CodexDynamicToolCallResponse> ExecuteAsync(
        CodexDynamicToolCallRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = DateTimeOffset.UtcNow;

        if (!_toolPolicy.IsDynamicToolAllowed(request.Tool))
        {
            _logger?.LogInformation(
                "{event_type} {event_status} {tool_name} {latency_ms}",
                "codex.dynamic_tool.execution",
                "denied",
                request.Tool,
                (DateTimeOffset.UtcNow - start).TotalMilliseconds);
            return Failure($"Tool '{request.Tool}' is not enabled for this session.");
        }

        if (!_handlersByName.TryGetValue(request.Tool, out var handler))
        {
            _logger?.LogInformation(
                "{event_type} {event_status} {tool_name} {latency_ms}",
                "codex.dynamic_tool.execution",
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
                "codex.dynamic_tool.execution",
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
                "codex.dynamic_tool.execution",
                "failed",
                request.Tool);
            return Failure(ex.Message);
        }
    }

    private static CodexDynamicToolCallResponse Success(string text)
    {
        return new CodexDynamicToolCallResponse
        {
            Success = true,
            ContentItems =
            [
                new CodexDynamicToolContentItem
                {
                    Type = "input_text",
                    Text = text,
                },
            ],
        };
    }

    private static CodexDynamicToolCallResponse Failure(string message)
    {
        return new CodexDynamicToolCallResponse
        {
            Success = false,
            ContentItems =
            [
                new CodexDynamicToolContentItem
                {
                    Type = "input_text",
                    Text = message,
                },
            ],
        };
    }
}
