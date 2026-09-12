using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>JSON-only command transport. Configured bootstrap resolves the trusted run scope before dispatch.</summary>
internal static class WorkflowScriptHost
{
    private const int MaximumInputCharacters = 8 * 1024 * 1024;

    /// <summary>Passes resolved host configuration to trusted child operations, including command-line overrides.</summary>
    internal static Dictionary<string, string> BuildEnvironment(
        IConfiguration configuration,
        IReadOnlyDictionary<string, string> canonicalPaths
    )
    {
        var result = new Dictionary<string, string>(canonicalPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { "CodeReviewDaemon", "SandboxGateway", "Auth", "WorkflowPublication" })
        {
            foreach (var setting in configuration.GetSection(section).AsEnumerable())
            {
                if (setting.Value is not null && !setting.Key.Split(':').Any(part => part.StartsWith('_')))
                    result.TryAdd(setting.Key.Replace(":", "__", StringComparison.Ordinal), setting.Value);
            }
        }
        return result;
    }

    /// <summary>Returns 0 on success, 2 for invalid input, 1 for operation failure, and 130 for cancellation.</summary>
    public static async Task<int> RunAsync(
        string operation,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, JsonObject, JsonNode, CancellationToken, Task<JsonNode>> dispatch,
        CancellationToken cancellationToken
    )
    {
        JsonObject envelope;
        try
        {
            var json = await ReadBoundedAsync(stdin, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            envelope = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Expected an object envelope.");
            RequireFields(envelope, "Context", "Input");
            var context = envelope["Context"] as JsonObject ?? throw new JsonException("Context must be an object.");
            RequireFields(context, "RunId", "StepId", "Attempt", "RunDirectory");
            foreach (var field in new[] { "RunId", "StepId", "RunDirectory" })
            {
                if (
                    context[field] is not JsonValue value
                    || !value.TryGetValue<string>(out var text)
                    || string.IsNullOrWhiteSpace(text)
                )
                {
                    throw new JsonException($"Context.{field} must be a nonempty string.");
                }
            }
            if (context["Attempt"] is not JsonValue attempt || !attempt.TryGetValue<int>(out var number) || number < 1)
            {
                throw new JsonException("Context.Attempt must be a positive integer.");
            }
            if (envelope["Input"] is null)
            {
                throw new JsonException("Input must be a non-null JSON value.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            await stderr.WriteLineAsync($"Invalid workflow input: {exception.Message}").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var result =
                await dispatch(operation, (JsonObject)envelope["Context"]!, envelope["Input"]!, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Operation returned no JSON result.");
            await stdout.WriteLineAsync(result.ToJsonString()).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("Workflow operation cancelled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception)
        {
            await stderr.WriteLineAsync($"Workflow operation failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (builder.Length + count > MaximumInputCharacters)
            {
                throw new JsonException("Workflow input exceeds the size limit.");
            }
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }

    private static void RequireFields(JsonObject value, params string[] fields)
    {
        if (value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field)))
        {
            throw new JsonException($"Expected exactly these fields: {string.Join(", ", fields)}.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException($"Duplicate JSON property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}
