using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;

/// <summary>
/// Exposes the <c>NotifyClient</c> tool: an ad-hoc, non-blocking notification pushed straight to the
/// client. Unlike every existing <see cref="NotifyMessage"/> producer (sub-agent completion, context
/// discovery, workflow completion — see <see cref="NotifyKinds"/>), which deliver via
/// <c>MultiTurnAgentLoop.SendAsync</c> (starting or injecting a turn), this tool bypasses the input
/// queue entirely: it never defers and never triggers a run of its own. The injected
/// <c>deliver</c> delegate (wired by the owning loop to its own protected
/// <c>AddToHistory</c> + <c>PublishToAllAsync</c>) persists the notification and publishes it live,
/// exactly mirroring the narrow resolve/notify delegate-injection pattern <c>TriggerRuntime</c> uses
/// (issue #246).
/// </summary>
public sealed class NotifyClientToolProvider : IFunctionProvider
{
    /// <summary>Tool name.</summary>
    public const string ToolName = "NotifyClient";

    private readonly Func<NotifyMessage, CancellationToken, ValueTask> _deliver;

    public NotifyClientToolProvider(Func<NotifyMessage, CancellationToken, ValueTask> deliver)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        _deliver = deliver;
    }

    public string ProviderName => "ClientTools";

    /// <summary>Low priority (high number) so domain tools take precedence on key conflicts.</summary>
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return new FunctionDescriptor
        {
            Contract = BuildContract(),
            Handler = HandleAsync,
            ProviderName = ProviderName,
        };
    }

    private static FunctionContract BuildContract() => new()
    {
        Name = ToolName,
        Description =
            "Push an ad-hoc, non-blocking notification to the client (e.g. progress update, heads-up "
            + "about something noteworthy). Unlike AskUserQuestion, this does NOT pause the run — it "
            + "returns immediately and the conversation continues.",
        Parameters =
        [
            new FunctionParameterContract
            {
                Name = "message",
                Description = "The notification text shown to the client.",
                ParameterType = new JsonSchemaObject { Type = new("string") },
                IsRequired = true,
            },
            new FunctionParameterContract
            {
                Name = "label",
                Description = "Optional short label/title for the notification.",
                ParameterType = new JsonSchemaObject { Type = new("string") },
                IsRequired = false,
            },
        ],
    };

    private async Task<ToolHandlerResult> HandleAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        if (!TryParseArgs(argsJson, out var message, out var label, out var errorCode, out var errorMessage))
        {
            return ToolHandlerResult.FromError(
                JsonSerializer.Serialize(new { status = "rejected", reason = errorCode, message = errorMessage }),
                errorCode);
        }

        var notify = NotifyMessage.Create(
            NotifyKinds.ClientNotification,
            detail: message,
            sourceToolName: ToolName,
            sourceToolCallId: context.ToolCallId,
            label: label);

        await _deliver(notify, cancellationToken);

        return ToolHandlerResult.FromText(JsonSerializer.Serialize(new { status = "sent" }));
    }

    private static bool TryParseArgs(
        string? argsJson,
        out string? message,
        out string? label,
        out string? errorCode,
        out string? errorMessage)
    {
        message = null;
        label = null;
        errorCode = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(argsJson))
        {
            errorCode = "missing_message";
            errorMessage = "NotifyClient requires a non-empty 'message'.";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(argsJson);
        }
        catch (JsonException)
        {
            errorCode = "invalid_args";
            errorMessage = "NotifyClient arguments are not valid JSON.";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_args";
                errorMessage = "NotifyClient arguments must be a JSON object.";
                return false;
            }

            message = root.TryGetProperty("message", out var messageEl)
                && messageEl.ValueKind == JsonValueKind.String
                ? messageEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(message))
            {
                errorCode = "missing_message";
                errorMessage = "NotifyClient requires a non-empty 'message'.";
                return false;
            }

            label = root.TryGetProperty("label", out var labelEl) && labelEl.ValueKind == JsonValueKind.String
                ? labelEl.GetString()
                : null;
            return true;
        }
    }
}
