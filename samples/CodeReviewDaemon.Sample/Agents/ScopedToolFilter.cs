using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Builds the review agent's tool registry for the redesigned reviewer: keeps the same read-only
/// tools <see cref="ReadOnlyToolFilter"/> keeps, and — only when <c>writableAllow</c> is non-empty —
/// also copies the listed writable tools across. <c>Write</c>/<c>Edit</c> are wrapped so a
/// prompt-injected agent cannot escape the PR notes dir / scratch dir: the wrapper inspects the
/// <c>file_path</c> argument and short-circuits with a tool-error result before the real (gateway)
/// handler ever runs. <c>Bash</c> (and any other writable tool) is copied through unwrapped — it is
/// bounded elsewhere (egress policy + the commit gate), not by this filter.
/// </summary>
internal static class ScopedToolFilter
{
    private static readonly HashSet<string> PathScopedToolNames = new(StringComparer.Ordinal) { "Write", "Edit" };

    public static void Apply(
        FunctionRegistry source,
        FunctionRegistry target,
        IReadOnlyList<string> readOnlyAllow,
        IReadOnlyList<string> writableAllow,
        string notesDir,
        string scratchDir)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(readOnlyAllow);
        ArgumentNullException.ThrowIfNull(writableAllow);
        ArgumentNullException.ThrowIfNull(notesDir);
        ArgumentNullException.ThrowIfNull(scratchDir);

        // Read-only tools are handled identically regardless of writableAllow — with writableAllow
        // empty this call is the ENTIRE behavior, so this produces byte-identical results to
        // ReadOnlyToolFilter.Apply.
        ReadOnlyToolFilter.Apply(source, target, readOnlyAllow);

        if (writableAllow.Count == 0)
        {
            return;
        }

        var allowedWritable = new HashSet<string>(writableAllow, StringComparer.Ordinal);
        var (contracts, handlers) = source.Build();
        foreach (var contract in contracts)
        {
            if (!allowedWritable.Contains(contract.Name) || !handlers.TryGetValue(contract.Name, out var handler))
            {
                continue;
            }

            var registeredHandler = PathScopedToolNames.Contains(contract.Name)
                ? WrapPathScoped(handler, notesDir, scratchDir)
                : handler;

            _ = target.AddFunction(contract, registeredHandler, "sandbox");
        }
    }

    /// <summary>
    /// Wraps a <c>Write</c>/<c>Edit</c> handler so it is only ever invoked for a <c>file_path</c>
    /// that resolves under <paramref name="notesDir"/> or <paramref name="scratchDir"/>. Any other
    /// path — missing, traversing via <c>..</c>, or simply outside both roots — is rejected with a
    /// tool-error result and the real handler is never called.
    /// </summary>
    private static ToolHandler WrapPathScoped(ToolHandler inner, string notesDir, string scratchDir)
    {
        return async (argsJson, context, cancellationToken) =>
        {
            var filePath = ExtractFilePath(argsJson);
            if (filePath is null || !IsWritablePath(filePath, notesDir, scratchDir))
            {
                return ToolHandlerResult.FromError(
                    $"scoped-write: '{filePath}' is outside the writable roots ({notesDir}, {scratchDir})");
            }

            return await inner(argsJson, context, cancellationToken).ConfigureAwait(false);
        };
    }

    private static string? ExtractFilePath(string argsJson)
    {
        if (string.IsNullOrEmpty(argsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(argsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("file_path", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsWritablePath(string filePath, string notesDir, string scratchDir)
    {
        var normalized = Normalize(filePath);
        if (HasParentTraversal(normalized))
        {
            return false;
        }

        return IsUnder(normalized, Normalize(notesDir)) || IsUnder(normalized, Normalize(scratchDir));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static bool HasParentTraversal(string normalizedPath) =>
        normalizedPath.Split('/').Any(segment => segment == "..");

    private static bool IsUnder(string path, string root)
    {
        var trimmedRoot = root.TrimEnd('/');
        return path == trimmedRoot || path.StartsWith(trimmedRoot + "/", StringComparison.Ordinal);
    }
}
