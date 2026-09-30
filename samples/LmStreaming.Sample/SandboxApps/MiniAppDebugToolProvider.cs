using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>Conversation-bound tools for diagnosing Mini Web Apps in their own sandbox.</summary>
public sealed class MiniAppDebugToolProvider : IFunctionProvider
{
    public const string ListToolName = "ListMiniApps";
    public const string InspectToolName = "InspectMiniApp";
    public const string TestRequestToolName = "TestMiniAppRequest";
    public static readonly IReadOnlyList<string> ToolNames = [ListToolName, InspectToolName, TestRequestToolName];

    private readonly IWorkspaceFileBrowser _browser;
    private readonly SandboxAppDiscovery _discovery;
    private readonly string _sessionId;
    private readonly string _workspaceId;
    private readonly string _serverName;

    public MiniAppDebugToolProvider(
        IWorkspaceFileBrowser browser,
        SandboxAppDiscovery discovery,
        string sessionId,
        string workspaceId,
        string serverName
    )
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        _sessionId = sessionId;
        _workspaceId = workspaceId;
        _serverName = serverName;
    }

    public string ProviderName => "MiniAppDebug";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return Describe(
            ListToolName,
            "List registered Mini Web Apps in this conversation's workspace.",
            [],
            ListAsync
        );
        yield return Describe(
            InspectToolName,
            "Inspect one app's manifest and files; optionally read one bounded file.",
            [Parameter("app_id", true), Parameter("file")],
            InspectAsync
        );
        yield return Describe(
            TestRequestToolName,
            "Run a bounded app request and capture stdout and stderr.",
            [
                Parameter("app_id", true),
                Parameter("method"),
                Parameter("path"),
                Parameter("query"),
                Parameter("body"),
                Parameter("content_type"),
            ],
            TestRequestAsync
        );
    }

    private FunctionDescriptor Describe(
        string name,
        string description,
        IReadOnlyList<FunctionParameterContract> parameters,
        ToolHandler handler
    ) =>
        new()
        {
            Contract = new FunctionContract
            {
                Name = name,
                Description = description,
                Parameters = parameters,
            },
            Handler = handler,
            ProviderName = ProviderName,
        };

    private static FunctionParameterContract Parameter(string name, bool required = false) =>
        new()
        {
            Name = name,
            Description = name switch
            {
                "app_id" => "Registered app ID in this conversation's workspace.",
                "file" => "Optional single file name in the app directory, up to 16 KiB; no paths.",
                "method" => "GET or POST. Defaults to GET.",
                "path" => "Local request path beginning with /. Defaults to /; no traversal or URL.",
                "query" => "Optional query string without ?. Up to 2048 characters.",
                "body" => "Optional UTF-8 POST body, up to 64 KiB.",
                "content_type" => "Optional POST content type. Defaults to text/plain.",
                _ => name,
            },
            ParameterType = new JsonSchemaObject { Type = new("string") },
            IsRequired = required,
        };

    private async Task<ToolHandlerResult> ListAsync(string args, ToolCallContext _, CancellationToken ct)
    {
        try
        {
            var apps = await _discovery.ListAsync(_sessionId, _workspaceId, ct);
            return ToolHandlerResult.FromText(
                JsonSerializer.Serialize(
                    apps.Select(a => new
                    {
                        id = a.Id,
                        name = a.Name,
                        link = a.Link,
                    })
                )
            );
        }
        catch (Exception ex) when (ex is SandboxException or SandboxSessionUnavailableException)
        {
            return ToolHandlerResult.FromError($"Could not list Mini Apps: {ex.Message}", "sandbox_unavailable");
        }
    }

    private async Task<ToolHandlerResult> InspectAsync(string args, ToolCallContext _, CancellationToken ct)
    {
        if (!TryReadArgs(args, out var input, out var error))
            return error!;
        using (input)
        {
            var appId = ReadString(input.RootElement, "app_id");
            var file = ReadString(input.RootElement, "file");
            if (!ValidId(appId) || (file is not null && !ValidFile(file)))
                return Invalid("Invalid app_id or file.");
            try
            {
                var app = await _discovery.ResolveAsync(_sessionId, _workspaceId, appId!, ct);
                if (app is null)
                    return ToolHandlerResult.FromError("No registered Mini App has that ID.", "app_not_found");
                var entries = await _browser.ListWorkspaceDirectoryAsync(_sessionId, $"mini-web-apps/{appId}", ct);
                if (entries.Count > 128)
                    return ToolHandlerResult.FromError(
                        "App directory has too many entries to inspect in one call.",
                        "too_many_files"
                    );
                var manifest = await _browser.ReadWorkspaceFileBytesAsync(
                    _sessionId,
                    $"mini-web-apps/{appId}/mini-web-app.json",
                    8192,
                    ct
                );
                string? fileContent = null;
                if (file is not null)
                {
                    var entry = entries.SingleOrDefault(e => e.Name == file);
                    if (
                        entry is null
                        || entry.Type != SandboxEntryType.File
                        || entry.NameLossy
                        || entry.Size is > 16384
                    )
                        return ToolHandlerResult.FromError(
                            "That app file is not available for bounded inspection.",
                            "file_not_found"
                        );
                    var bytes = await _browser.ReadWorkspaceFileBytesAsync(
                        _sessionId,
                        $"mini-web-apps/{appId}/{file}",
                        16384,
                        ct
                    );
                    fileContent = Encoding.UTF8.GetString(bytes);
                }
                return ToolHandlerResult.FromText(
                    JsonSerializer.Serialize(
                        new
                        {
                            id = app.Id,
                            name = app.Name,
                            link = app.Link,
                            manifest = Encoding.UTF8.GetString(manifest),
                            files = entries.Select(e => new
                            {
                                name = e.Name,
                                type = e.Type.ToString(),
                                size = e.Size,
                                name_lossy = e.NameLossy,
                            }),
                            file_content = fileContent,
                        }
                    )
                );
            }
            catch (Exception ex) when (ex is SandboxException or SandboxSessionUnavailableException)
            {
                return ToolHandlerResult.FromError($"Could not inspect Mini App: {ex.Message}", "sandbox_unavailable");
            }
        }
    }

    private async Task<ToolHandlerResult> TestRequestAsync(string args, ToolCallContext _, CancellationToken ct)
    {
        if (!TryReadArgs(args, out var input, out var error))
            return error!;
        using (input)
        {
            var root = input.RootElement;
            var appId = ReadString(root, "app_id");
            var method = ReadString(root, "method") ?? "GET";
            var path = ReadString(root, "path") ?? "/";
            var query = ReadString(root, "query") ?? "";
            var body = ReadString(root, "body") ?? "";
            var contentType = ReadString(root, "content_type") ?? "text/plain";
            if (
                !ValidId(appId)
                || method is not ("GET" or "POST")
                || !ValidPath(path)
                || query.Length > 2048
                || query.StartsWith('?')
                || query.Any(char.IsControl)
                || query.Contains('#')
                || contentType.Length > 128
                || contentType.Any(char.IsControl)
                || (method == "GET" && body.Length != 0)
            )
                return Invalid("Use a valid app_id, GET or POST, local path, and bounded query/content type.");
            var stdin = Encoding.UTF8.GetBytes(body);
            if (stdin.Length > 65536)
                return Invalid("POST body exceeds 64 KiB.");
            try
            {
                var app = await _discovery.ResolveAsync(_sessionId, _workspaceId, appId!, ct);
                if (app is null)
                    return ToolHandlerResult.FromError("No registered Mini App has that ID.", "app_not_found");
                if (!await _browser.SupportsStreamingAsync(_sessionId, ct))
                    return ToolHandlerResult.FromError(
                        "The sandbox gateway does not support streaming operations.",
                        "streaming_unavailable"
                    );
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var env = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["REQUEST_METHOD"] = method,
                    ["PATH_INFO"] = path,
                    ["QUERY_STRING"] = query,
                    ["CONTENT_TYPE"] = method == "POST" ? contentType : "",
                    ["CONTENT_LENGTH"] = stdin.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["HTTPS"] = "on",
                    ["SERVER_NAME"] = _serverName,
                    ["SANDBOX_APP_CSRF_TOKEN"] = token,
                };
                if (method == "POST")
                    env["HTTP_X_CSRF_TOKEN"] = token;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var stdout = new MemoryStream();
                using var stderr = new MemoryStream();
                var command = new SandboxCommand(
                    [app.Definition.Executable, .. app.Definition.Arguments],
                    app.Definition.WorkingDirectory
                );
                var result = await _browser.ExecuteWorkspaceCommandStreamingAsync(
                    _sessionId,
                    command,
                    (chunk, _) =>
                    {
                        var target = chunk.Stream == SandboxOutputStream.Stdout ? stdout : stderr;
                        if (stdout.Length + stderr.Length + chunk.Data.Length > 256 * 1024)
                            throw new InvalidDataException("Mini App output exceeds 256 KiB.");
                        target.Write(chunk.Data.Span);
                        return ValueTask.CompletedTask;
                    },
                    stdin,
                    env,
                    256 * 1024,
                    timeout.Token
                );
                return ToolHandlerResult.FromText(
                    JsonSerializer.Serialize(
                        new
                        {
                            app_id = appId,
                            method,
                            path,
                            exit_code = result.ExitCode,
                            stdout = Encoding.UTF8.GetString(stdout.ToArray()),
                            stderr = Encoding.UTF8.GetString(stderr.ToArray()),
                            stdout_bytes = result.StdoutBytes,
                            stderr_bytes = result.StderrBytes,
                        }
                    )
                );
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ToolHandlerResult.FromError("Mini App test exceeded 30 seconds.", "timeout");
            }
            catch (Exception ex)
                when (ex is SandboxException or SandboxSessionUnavailableException or InvalidDataException)
            {
                return ToolHandlerResult.FromError($"Mini App test failed: {ex.Message}", "sandbox_error");
            }
        }
    }

    private static bool TryReadArgs(string args, out JsonDocument input, out ToolHandlerResult? error)
    {
        try
        {
            input = JsonDocument.Parse(args);
            if (input.RootElement.ValueKind == JsonValueKind.Object)
            {
                error = null;
                return true;
            }
            input.Dispose();
        }
        catch (JsonException) { }
        input = null!;
        error = Invalid("Tool arguments must be a JSON object.");
        return false;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ValidId(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool ValidFile(string value) =>
        value is { Length: > 0 and <= 128 }
        && value is not ("." or "..")
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool ValidPath(string path) =>
        path is { Length: > 0 and <= 2048 }
        && path.StartsWith('/')
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains('\\')
        && !path.Contains('?')
        && !path.Contains('#')
        && !path.Any(char.IsControl)
        && path.Split('/').All(segment => segment is not ("." or "..") && !segment.Contains('%'));

    private static ToolHandlerResult Invalid(string message) => ToolHandlerResult.FromError(message, "invalid_args");
}
