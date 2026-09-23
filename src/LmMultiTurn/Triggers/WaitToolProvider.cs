using System.Text;
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

    /// <summary>
    /// What a blocking <c>Wait</c> returns when the loop settles the parked call early because
    /// something arrived that has to run now. The wait itself stays armed: this is a change of
    /// ending, not a cancellation.
    /// </summary>
    /// <remarks>
    /// Lives here, next to the contract that describes it, because the model reads both: a
    /// description promising only one ending would make this text read as a failure, and the model
    /// would arm a second wait for an event the first one is still watching for.
    /// </remarks>
    public const string EarlySettlePlaceholder =
        "Wait still armed; its result will arrive as a message when the event fires or the timeout is reached";

    /// <summary>
    /// The element the late trigger result is injected under once the call has been settled early.
    /// Deliberately not the question tool's <c>user-answer</c>: nobody answered anything.
    /// </summary>
    public const string InjectionTag = "wait-result";

    /// <summary>
    /// <see cref="EarlySettlePlaceholder"/> in this tool's own envelope shape, with a named non-error
    /// <c>status</c> - the same envelope family as the statuses a wait resolves with, so a model
    /// branching on <c>status</c> sees a legitimate outcome instead of a string to parse.
    /// </summary>
    public static readonly string EarlySettleResultJson = JsonSerializer.Serialize(
        new { status = EarlySettlePlaceholders.EarlySettleStatus, message = EarlySettlePlaceholder }
    );

    /// <summary>
    /// Restates the wait that was armed, for the injected message that carries the trigger's result
    /// after an early settle. Falls back to the raw arguments when they do not parse - an unreadable
    /// request is still better context than none.
    /// </summary>
    internal static string RenderRequestForInjection(string? argsJson)
    {
        if (!WaitToolArgs.TryParse(argsJson, out var parsed))
        {
            return argsJson ?? string.Empty;
        }

        var sb = new StringBuilder().Append("Wait on kind '").Append(parsed.Kind).Append('\'');
        if (!string.IsNullOrWhiteSpace(parsed.Label))
        {
            _ = sb.Append(" (").Append(parsed.Label).Append(')');
        }

        return sb.Append(" with args ").Append(parsed.ArgsJson).Append(", timeout ").Append(parsed.Timeout).ToString();
    }

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
                + "The run parks after this call. It has TWO possible endings and you must handle "
                + "both.\n"
                + "1. The event fires, or the timeout is reached, while you are still parked: that "
                + "result becomes this tool's return value and you continue from it.\n"
                + "2. Something else arrives that has to run first (a message from the human, a "
                + "message from another agent, a sub-agent or workflow finishing). The call then "
                + "returns {\"status\":\""
                + EarlySettlePlaceholders.EarlySettleStatus
                + "\"} with the message \""
                + EarlySettlePlaceholder
                + "\". This is NOT a failure and NOT a timeout. The wait is STILL ARMED, so do NOT "
                + "arm it again: its result will arrive later as its own <"
                + InjectionTag
                + "> message. Get on with whatever arrived.\n\n"
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
                new FunctionParameterContract
                {
                    Name = "mode",
                    Description =
                        "\"block\" (default) parks the run until the event fires or times out — the result is "
                        + "this call's return value. \"notify\" arms without parking: each fire is delivered as a "
                        + "new message and the wait stays armed for more fires until maxFires or timeout.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "maxFires",
                    Description =
                        "Notify mode only: stop after this many fires (positive integer). Omit for unlimited "
                        + "(bounded only by timeout). Ignored in block mode.",
                    ParameterType = new JsonSchemaObject { Type = new("integer") },
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
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(context.ToolCallId))
        {
            return ToolHandlerResult.FromError(
                Reject("missing_tool_call_id", "Wait requires a tool_call_id to correlate the deferred result."),
                "missing_tool_call_id"
            );
        }

        if (!WaitToolArgs.TryParse(argsJson, out var parsed))
        {
            return ToolHandlerResult.FromError(
                Reject("invalid_args", "Wait requires 'kind' and 'timeout'."),
                "invalid_args"
            );
        }

        var result = await _runtime.ArmAsync(
            context.ToolCallId,
            parsed.Kind,
            parsed.ArgsJson,
            parsed.Timeout,
            parsed.Label,
            parsed.Mode,
            parsed.MaxFires,
            cancellationToken
        );

        if (!result.IsArmed)
        {
            return ToolHandlerResult.FromError(
                Reject(result.Reason ?? "rejected", result.Message ?? "Wait was rejected."),
                result.Reason
            );
        }

        if (parsed.Mode == WaitMode.Notify)
        {
            // Notify arms do NOT park the run; acknowledge immediately. Fires arrive later as
            // <trigger> turns via the loop's queue gate.
            return ToolHandlerResult.FromText(
                JsonSerializer.Serialize(
                    new
                    {
                        status = "armed",
                        mode = "notify",
                        waitId = context.ToolCallId,
                        kind = parsed.Kind,
                        maxFires = parsed.MaxFires,
                    }
                )
            );
        }

        // Block mode: park the run; the runtime resolves this tool call when the wait terminates.
        return new ToolHandlerResult.Deferred();
    }

    private async Task<ToolHandlerResult> HandleCancelWaitAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        var (id, label, kind) = ParseCancelArgs(argsJson);
        var cancelled = await _runtime.CancelWaitsAsync(id, label, kind, cancellationToken);
        return ToolHandlerResult.FromText(JsonSerializer.Serialize(new { status = "resolved", cancelled }));
    }

    private Task<ToolHandlerResult> HandleListWaitsAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        var waits = _runtime.ListWaits();
        var payload = JsonSerializer.Serialize(new { waits, registeredKinds = _runtime.RegisteredKinds });
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
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static string Reject(string reason, string message) =>
        JsonSerializer.Serialize(
            new
            {
                status = "rejected",
                reason,
                message,
            }
        );
}
