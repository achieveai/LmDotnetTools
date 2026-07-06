using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Exposes the generic <c>Wait</c> / <c>CancelWait</c> / <c>ListWaits</c> tools, kind-dispatched
/// onto a <see cref="TriggerRuntime"/>. Registered as an <see cref="IFunctionProvider"/> so the
/// tools join the agent's function registry alongside everything else. <c>Wait</c> parks the run
/// (returns <c>Deferred()</c>); <c>CancelWait</c>/<c>ListWaits</c> resolve synchronously.
/// </summary>
public sealed class WaitToolProvider : IFunctionProvider
{
    /// <summary>Tool name used for the block <c>Wait</c> — also the deferred-entry function name matched on restart.</summary>
    public const string WaitToolName = "Wait";

    private readonly TriggerRuntime _runtime;

    public WaitToolProvider(TriggerRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public string ProviderName => "TriggerTools";

    /// <summary>Low priority (high number) so domain tools take precedence on key conflicts.</summary>
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return CreateWaitDescriptor();
        yield return CreateCancelWaitDescriptor();
        yield return CreateListWaitsDescriptor();
    }

    private FunctionDescriptor CreateWaitDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = WaitToolName,
            Description =
                "Pause and wait for a background/scheduled event, then resume with its result. "
                + "The run parks after this call and continues automatically once the event fires "
                + "or the timeout is reached — the result becomes this tool's return value.\n\n"
                + _runtime.DescribeKindsForToolContract(),
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "kind",
                    Description = "Which registered trigger kind to wait on (see the list above).",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "args",
                    Description = "Per-kind options object (see each kind's args shape above).",
                    ParameterType = new JsonSchemaObject { Type = new("object") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "timeout",
                    Description =
                        "Required safety ceiling: a duration (e.g. \"10m\", \"30s\") or an absolute "
                        + "ISO-8601 time. If reached before the event fires, the wait resolves with "
                        + "status \"timed_out\".",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "label",
                    Description = "Optional short, self-describing label shown in ListWaits.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleWaitAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionDescriptor CreateCancelWaitDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = "CancelWait",
            Description =
                "Cancel one or more armed waits by id, label, or kind. Total and idempotent — "
                + "cancelling an unknown or already-finished wait is a no-op. A cancelled block "
                + "wait resolves immediately with status \"cancelled\".",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "id",
                    Description = "The waitId to cancel (from ListWaits).",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "label",
                    Description = "Cancel all waits with this label.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "kind",
                    Description = "Cancel all waits of this kind.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleCancelWaitAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionDescriptor CreateListWaitsDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = "ListWaits",
            Description = "List all currently-armed waits (id, kind, label, state, timing) and the registered kinds.",
            Parameters = [],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleListWaitsAsync,
            ProviderName = ProviderName,
        };
    }

    private async Task<ToolHandlerResult> HandleWaitAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.ToolCallId))
        {
            return ToolHandlerResult.FromError(
                Reject("missing_tool_call_id", "Wait requires a tool_call_id to correlate the deferred result."),
                "missing_tool_call_id");
        }

        if (!WaitToolArgs.TryParse(argsJson, out var parsed))
        {
            return ToolHandlerResult.FromError(
                Reject("invalid_args", "Wait requires 'kind' and 'timeout'."),
                "invalid_args");
        }

        var result = await _runtime.ArmAsync(
            context.ToolCallId,
            parsed.Kind,
            parsed.ArgsJson,
            parsed.Timeout,
            parsed.Label,
            parsed.Mode,
            parsed.MaxFires,
            cancellationToken);

        if (result.IsArmed)
        {
            // Park the run; the runtime resolves this tool call when the wait terminates.
            return new ToolHandlerResult.Deferred();
        }

        return ToolHandlerResult.FromError(
            Reject(result.Reason ?? "rejected", result.Message ?? "Wait was rejected."),
            result.Reason);
    }

    private async Task<ToolHandlerResult> HandleCancelWaitAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        var (id, label, kind) = ParseCancelArgs(argsJson);
        var cancelled = await _runtime.CancelWaitsAsync(id, label, kind, cancellationToken);
        return ToolHandlerResult.FromText(
            JsonSerializer.Serialize(new { status = "resolved", cancelled }));
    }

    private Task<ToolHandlerResult> HandleListWaitsAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        var waits = _runtime.ListWaits();
        var payload = JsonSerializer.Serialize(new
        {
            waits,
            registeredKinds = _runtime.RegisteredKinds,
        });
        return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(payload));
    }

    private static (string? Id, string? Label, string? Kind) ParseCancelArgs(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            return (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            return (Get(root, "id"), Get(root, "label"), Get(root, "kind"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        static string? Get(JsonElement root, string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
    }

    private static string Reject(string reason, string message) =>
        JsonSerializer.Serialize(new { status = "rejected", reason, message });
}
