using System.Net.Http.Headers;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using LmStreaming.Sample.Configuration;

namespace LmStreaming.Sample.Services;

/// <summary>Forwards parent-bound publication calls to the daemon that owns scope and durable receipts.</summary>
public sealed class WorkflowPublicationToolProvider(
    HttpClient client,
    WorkflowPublicationEndpoint options,
    string threadId,
    Func<string?> currentRunId
) : IFunctionProvider, IAsyncDisposable
{
    public static readonly IReadOnlyList<string> ToolNames =
    [
        "review_publish_summary",
        "review_publish_inline",
        "review_reply",
    ];
    public string ProviderName => "WorkflowPublication";
    public int Priority => 100;

    /// <summary>Matches the API-provider path that builds the host's native tool registry.</summary>
    internal static bool SupportsProvider(ProviderRegistry registry, string providerId) =>
        registry.IsAvailable(providerId)
        && (
            providerId.Trim().ToLowerInvariant() is "openai" or "anthropic" or "test" or "test-anthropic"
            || registry.TryGetCopilotModel(providerId, out _)
            || registry.TryGetAnthropicCompatModel(providerId, out _)
        );

    /// <summary>The parent pool owns this provider and releases its callback client on eviction.</summary>
    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        foreach (var name in ToolNames)
        {
            var parameters = new List<FunctionParameterContract>
            {
                Parameter(
                    "actionId",
                    "Stable identifier for this publication action. Reuse it on any retry; never invent a new id to retry an uncertain action."
                ),
                Parameter("body", "The complete comment text to publish."),
            };
            if (name == "review_publish_inline")
            {
                parameters.Add(Parameter("path", "Repository-relative file path."));
                parameters.Add(Parameter("line", "Positive diff line number.", type: "integer"));
                parameters.Add(Parameter("side", "Diff side, LEFT or RIGHT."));
            }
            else if (name == "review_reply")
            {
                parameters.Add(Parameter("providerThreadId", "Existing provider discussion thread id."));
                parameters.Add(Parameter("parentCommentId", "Existing parent comment id within that discussion."));
            }
            yield return new FunctionDescriptor
            {
                Contract = new FunctionContract
                {
                    Name = name,
                    Description =
                        "Publish the supplied review comment within the currently authorized workflow step. "
                        + "The daemon verifies scope and returns its durable receipt. An unknown result is not confirmation of publication.",
                    Parameters = parameters,
                },
                Handler = (args, context, ct) => ForwardAsync(name, args, context, ct),
            };
        }
    }

    private async Task<ToolHandlerResult> ForwardAsync(
        string name,
        string argsJson,
        ToolCallContext context,
        CancellationToken ct
    )
    {
        var runId = currentRunId();
        if (
            !options.IsConfigured
            || string.IsNullOrWhiteSpace(threadId)
            || string.IsNullOrWhiteSpace(runId)
            || string.IsNullOrWhiteSpace(context.ToolCallId)
        )
            return ToolHandlerResult.FromError(
                "Publication is unavailable outside a bound active run.",
                "publication_denied"
            );

        JsonDocument arguments;
        try
        {
            arguments = JsonDocument.Parse(argsJson);
        }
        catch (JsonException)
        {
            return ToolHandlerResult.FromError("Publication arguments must be a JSON object.", "invalid_args");
        }
        using (arguments)
        {
            if (!ValidArguments(name, arguments.RootElement))
                return ToolHandlerResult.FromError(
                    "Publication arguments are missing, duplicated, invalid, or contain undeclared fields.",
                    "invalid_args"
                );

            using var request = new HttpRequestMessage(HttpMethod.Post, options.CallbackUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SharedSecret);
            request.Content = JsonContent.Create(
                new
                {
                    threadId,
                    runId,
                    toolCallId = context.ToolCallId,
                    toolName = name,
                    args = arguments.RootElement,
                }
            );
            try
            {
                // No retry: a lost response may follow a completed external write. Only the daemon's
                // durable actionId receipt can reconcile it. The production client also disables redirects.
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return Unknown();
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Unknown();
                var receipt = document.RootElement;
                if (receipt.TryGetProperty("error", out _))
                    return ToolHandlerResult.FromError(body, "publication_unknown");
                if (
                    !receipt.TryGetProperty("Status", out var status)
                    || status.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(status.GetString())
                    || !receipt.TryGetProperty("ReceiptId", out var id)
                    || id.ValueKind != JsonValueKind.Number
                    || !id.TryGetInt64(out var receiptId)
                    || receiptId < 1
                )
                    return Unknown();
                return ToolHandlerResult.FromText(body);
            }
            catch (Exception ex)
                when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                return Unknown();
            }
        }
    }

    private static ToolHandlerResult Unknown() =>
        ToolHandlerResult.FromError(
            "{\"error\":\"publication_unknown\",\"message\":\"Publication is unconfirmed. Preserve the same actionId for reconciliation.\"}",
            "publication_unknown"
        );

    private static bool ValidArguments(string name, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;
        string[] allowed = name switch
        {
            "review_publish_inline" => ["actionId", "body", "path", "line", "side"],
            "review_reply" => ["actionId", "body", "providerThreadId", "parentCommentId"],
            _ => ["actionId", "body"],
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                return false;
            if (property.Name == "line")
            {
                if (
                    property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out var line)
                    || line < 1
                )
                    return false;
            }
            else if (
                property.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.Value.GetString())
            )
                return false;
        }
        if (!seen.Contains("actionId") || !seen.Contains("body"))
            return false;
        return name switch
        {
            "review_publish_inline" => seen.Contains("path")
                && seen.Contains("line")
                && seen.Contains("side")
                && args.GetProperty("side").GetString() is "LEFT" or "RIGHT",
            "review_reply" => seen.Contains("providerThreadId") && seen.Contains("parentCommentId"),
            _ => true,
        };
    }

    private static FunctionParameterContract Parameter(
        string name,
        string description,
        bool required = true,
        string type = "string"
    ) =>
        new()
        {
            Name = name,
            Description = description,
            IsRequired = required,
            ParameterType = new JsonSchemaObject { Type = new(type) },
        };
}
