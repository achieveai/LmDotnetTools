using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.FileBrowser;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>Conversation-bound Chrome tools for Mini Apps and generated HTML.</summary>
public sealed class BrowserDebugToolProvider : IFunctionProvider, IAsyncDisposable
{
    public const string OpenToolName = "OpenDebugBrowser";
    public const string RunToolName = "RunBrowserTool";
    public const string CloseToolName = "CloseDebugBrowser";
    public static readonly IReadOnlyList<string> ToolNames = [OpenToolName, RunToolName, CloseToolName];

    private readonly BrowserDebugBindingStore _bindings;
    private readonly BrowserDebugAccess _access;
    private readonly IWorkspaceFileBrowser _files;
    private readonly SandboxAppDiscovery _discovery;
    private readonly IBrowserDebugGateway _gateway;
    private readonly string _threadId;
    private readonly string _workspaceId;
    private readonly string _sessionId;
    private readonly Principal? _principal;
    private readonly bool _miniAppsEnabled;
    private readonly ILogger<BrowserDebugToolProvider> _logger;
    private readonly Dictionary<string, BrowserDebugBinding> _owned = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _operations = new(1);
    private int _disposed;

    public BrowserDebugToolProvider(
        BrowserDebugBindingStore bindings,
        BrowserDebugAccess access,
        IWorkspaceFileBrowser files,
        SandboxAppDiscovery discovery,
        IBrowserDebugGateway gateway,
        string threadId,
        string workspaceId,
        string sessionId,
        Principal? principal,
        bool miniAppsEnabled = false,
        ILogger<BrowserDebugToolProvider>? logger = null
    )
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _threadId = threadId;
        _workspaceId = workspaceId;
        _sessionId = sessionId;
        _principal = principal;
        _miniAppsEnabled = miniAppsEnabled;
        _logger = logger ?? NullLogger<BrowserDebugToolProvider>.Instance;
    }

    public string ProviderName => "BrowserDebug";
    public int Priority => 100;

    public static string Description(string name) =>
        name switch
        {
            OpenToolName =>
                "Open a Mini App or workspace HTML file in sandbox Chrome, with page state and a screenshot.",
            RunToolName =>
                "Discover or run Playwright/DevTools actions on this preview; read console, network, DOM, or screenshots. Omit filename for inline evidence: Chrome artifact files are not workspace files. Return concise JSON from evaluation/code; use the screenshot action for images.",
            CloseToolName => "Close this preview's sandbox Chrome and invalidate its browser handle.",
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return Describe(
            OpenToolName,
            [Parameter("miniAppId"), Parameter("htmlFilePath"), Parameter("assetsRootPath")]
        );
        yield return Describe(
            RunToolName,
            [
                Parameter("browserHandle", true),
                Parameter("toolkit", true),
                Parameter("toolName"),
                Parameter("toolArguments", objectType: true),
            ]
        );
        yield return Describe(CloseToolName, [Parameter("browserHandle", true)]);
    }

    private FunctionDescriptor Describe(string name, IReadOnlyList<FunctionParameterContract> parameters) =>
        new()
        {
            Contract = new FunctionContract
            {
                Name = name,
                Description = Description(name),
                Parameters = parameters,
            },
            ProviderName = ProviderName,
            Handler = (args, _, ct) => ExecuteAsync(name, args, ct),
        };

    private static FunctionParameterContract Parameter(string name, bool required = false, bool objectType = false) =>
        new()
        {
            Name = name,
            IsRequired = required,
            Description = name switch
            {
                "miniAppId" =>
                    "Registered Mini App ID. For a Mini App, set htmlFilePath and assetsRootPath to null. Supply exactly one target.",
                "htmlFilePath" =>
                    "Workspace-relative HTML file. For HTML, set miniAppId to null; no registration required. Supply exactly one target.",
                "assetsRootPath" =>
                    "HTML asset boundary. Defaults to the HTML parent folder; '.' explicitly permits non-excluded files in this workspace. HTML must be inside it.",
                "browserHandle" =>
                    "Opaque handle returned by OpenDebugBrowser for this conversation. Never a gateway browser ID.",
                "toolkit" => "playwright or devtools.",
                "toolName" =>
                    "Upstream action name. Omit or set null to discover names, descriptions and JSON argument schemas.",
                "toolArguments" =>
                    "JSON object matching the discovered action schema. The host supplies the browser identity.",
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            },
            ParameterType = new JsonSchemaObject
            {
                Type = required
                    ? JsonSchemaTypeHelper.ToType(objectType ? "object" : "string")
                    : JsonSchemaTypeHelper.ToType([objectType ? "object" : "string", "null"]),
                AdditionalProperties = objectType,
            },
        };

    private async Task<ToolHandlerResult> ExecuteAsync(string name, string args, CancellationToken ct)
    {
        await _operations.WaitAsync(ct);
        try
        {
            if (_disposed != 0)
            {
                return ToolHandlerResult.FromError(
                    "This browser tool scope is closed. Open a preview in the active mode.",
                    "browser_closed"
                );
            }
            if (args.Length > 32768)
            {
                return ToolHandlerResult.FromError("Browser arguments exceed 32 KiB.", "invalid_args");
            }
            using var document = JsonDocument.Parse(args);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ToolHandlerResult.FromError("Browser arguments must be a JSON object.", "invalid_args");
            }
            var root = document.RootElement;
            var context = await _access.ResolveAsync(_threadId, _principal, name, ct);
            if (context.Status != 200 || context.WorkspaceId != _workspaceId || context.SessionId != _sessionId)
            {
                foreach (var binding in _owned.Values)
                {
                    _bindings.Revoke(binding);
                }
                return ToolHandlerResult.FromError(
                    "Browser access is unavailable for this conversation, workspace or mode.",
                    "browser_access_denied"
                );
            }
            return name switch
            {
                OpenToolName => await OpenAsync(root, ct),
                RunToolName => await RunAsync(root, ct),
                CloseToolName => await CloseAsync(root, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return ToolHandlerResult.FromError(
                "Use the documented browser arguments and workspace-relative paths.",
                "invalid_args"
            );
        }
        catch (Exception ex) when (ex is SandboxException or SandboxSessionUnavailableException or InvalidDataException)
        {
            _logger.LogWarning(
                ex,
                "Sandbox browser call {ToolName} failed: {ErrorKind}",
                name,
                (ex as SandboxException)?.Kind.ToString() ?? ex.GetType().Name
            );
            return ToolHandlerResult.FromError(
                "The sandbox browser call failed. Check gateway availability and reopen the preview.",
                "browser_unavailable"
            );
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task<ToolHandlerResult> OpenAsync(JsonElement input, CancellationToken ct)
    {
        var appId = String(input, "miniAppId");
        var html = String(input, "htmlFilePath");
        var root = String(input, "assetsRootPath");
        if ((appId is null) == (html is null) || (appId is not null && root is not null))
        {
            return ToolHandlerResult.FromError(
                "Supply exactly one of miniAppId or htmlFilePath. assetsRootPath applies only to HTML.",
                "invalid_args"
            );
        }
        if (_bindings.UnavailableReason is { } reason)
        {
            return ToolHandlerResult.FromError(reason, "browser_unavailable");
        }
        // Reclaim slots the agent can no longer use (expired, revoked, or a close the gateway did not
        // confirm) before enforcing the cap, so dead handles cannot lock out new previews.
        foreach (var stale in _owned.Values.Where(b => !IsLive(b)).ToArray())
        {
            await CleanupAsync(stale);
        }
        if (_owned.Count >= 8)
        {
            return ToolHandlerResult.FromError("Close an existing preview before opening another.", "browser_limit");
        }
        BrowserDebugTarget target;
        if (appId is not null)
        {
            if (!_miniAppsEnabled)
            {
                return ToolHandlerResult.FromError(
                    "Mini App browser targets require SandboxApps:Enabled.",
                    "mini_app_unavailable"
                );
            }
            var app = await _discovery.ResolveAsync(_sessionId, _workspaceId, appId, ct);
            if (app is null)
            {
                return ToolHandlerResult.FromError("No registered Mini App has that ID.", "app_not_found");
            }
            if (!await _files.SupportsStreamingAsync(_sessionId, ct))
            {
                return ToolHandlerResult.FromError(
                    "Mini App previews require sandbox streaming support.",
                    "streaming_unavailable"
                );
            }
            target = new(app.Definition, null, null);
        }
        else
        {
            html = WorkspacePath(html!);
            if (
                !new[] { ".html", ".htm", ".xhtml" }.Contains(Path.GetExtension(html), StringComparer.OrdinalIgnoreCase)
            )
            {
                return ToolHandlerResult.FromError("Choose an HTML file.", "invalid_args");
            }
            root =
                root == "." ? ""
                : root is null ? Parent(html)
                : WorkspacePath(root);
            if (
                (root.Length != 0 && !html.StartsWith(root + "/", StringComparison.Ordinal))
                || FilePreviewPolicy.IsUnderDotDirectory(html)
            )
            {
                return ToolHandlerResult.FromError(
                    "The HTML file must be inside its non-excluded asset folder.",
                    "invalid_args"
                );
            }
            var file = await WorkspacePathResolver.ResolveAsync(_files, _sessionId, html, ct);
            if (!file.Success || file.Type != SandboxEntryType.File)
            {
                return ToolHandlerResult.FromError(
                    "That HTML file is unavailable; directories and symlinks are not previewed.",
                    "file_not_found"
                );
            }
            if (file.Size is > FileBrowserLimits.MaxDownloadBytes)
            {
                return ToolHandlerResult.FromError("The HTML file exceeds the preview size limit.", "file_too_large");
            }
            target = new(null, file.ServerPath, root);
        }
        if (!await _gateway.SupportsBrowserAsync(ct))
        {
            return ToolHandlerResult.FromError(
                "This gateway does not advertise Browser. Configure a browser-enabled gateway.",
                "browser_unavailable"
            );
        }
        BrowserDebugBinding binding;
        try
        {
            binding = _bindings.Create(_threadId, _workspaceId, _sessionId, _principal, target);
        }
        catch (InvalidOperationException ex)
        {
            return ToolHandlerResult.FromError(ex.Message, "browser_unavailable");
        }
        _owned.Add(binding.Handle, binding);
        var opened = false;
        try
        {
            // Discovery establishes the incarnation before Chrome makes its first signed page request.
            var discovered = await _gateway.CallAsync(binding.BrowserId, "playwright", null, null, ct);
            if (IsError(discovered))
            {
                return Relay(binding, discovered);
            }
            if (!AcceptInstance(binding, discovered))
            {
                return Replaced(binding, discovered);
            }
            var navigated = await _gateway.CallAsync(
                binding.BrowserId,
                "playwright",
                "browser_navigate",
                JsonSerializer.SerializeToElement(new { url = binding.PreviewUrl }),
                ct
            );
            if (!AcceptInstance(binding, navigated))
            {
                return Replaced(binding, navigated);
            }
            if (IsError(navigated))
            {
                return Relay(binding, navigated);
            }
            var screenshot = await _gateway.CallAsync(
                binding.BrowserId,
                "playwright",
                "browser_take_screenshot",
                JsonSerializer.SerializeToElement(new { type = "png" }),
                ct
            );
            if (!AcceptInstance(binding, screenshot))
            {
                return Replaced(binding, screenshot);
            }
            if (IsError(screenshot))
            {
                return Relay(binding, screenshot);
            }
            opened = true;
            return Relay(binding, navigated, screenshot);
        }
        finally
        {
            if (!opened)
            {
                await CleanupAsync(binding);
            }
        }
    }

    private async Task<ToolHandlerResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        var handle = String(input, "browserHandle");
        var toolkit = String(input, "toolkit");
        var tool = String(input, "toolName");
        if (
            handle is null
            || toolkit is not ("playwright" or "devtools")
            || (
                input.TryGetProperty("toolArguments", out var arguments)
                && arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)
            )
        )
        {
            return ToolHandlerResult.FromError(
                "Use a browserHandle, playwright/devtools toolkit and JSON toolArguments.",
                "invalid_args"
            );
        }
        if (
            !_owned.ContainsKey(handle)
            || !_bindings.TryGetOwned(handle, _threadId, _workspaceId, _sessionId, out var binding)
        )
        {
            return ToolHandlerResult.FromError(
                "No live preview belongs to this handle. OpenDebugBrowser creates a fresh preview.",
                "browser_not_found"
            );
        }
        var result = await _gateway.CallAsync(
            binding!.BrowserId,
            toolkit,
            tool,
            input.TryGetProperty("toolArguments", out var args) && args.ValueKind != JsonValueKind.Null ? args : null,
            ct
        );
        if (!AcceptInstance(binding, result))
        {
            await CleanupAsync(binding);
            return Replaced(binding, result);
        }
        return Relay(binding, result);
    }

    private async Task<ToolHandlerResult> CloseAsync(JsonElement input, CancellationToken ct)
    {
        var handle = String(input, "browserHandle");
        if (handle is null || !_owned.TryGetValue(handle, out var binding))
        {
            return ToolHandlerResult.FromError("No preview belongs to this handle.", "browser_not_found");
        }
        // Preview routing is revoked at once, but the lease stays owned until the gateway confirms the
        // close: a failed close can then be retried with the same handle or released by DisposeAsync.
        _bindings.Revoke(binding);
        var closed = await _gateway.CloseAsync(binding.BrowserId, ct);
        _owned.Remove(handle);
        return ToolHandlerResult.FromText(
            JsonSerializer.Serialize(
                new
                {
                    browserHandle = handle,
                    closed,
                    handleInvalidated = true,
                }
            )
        );
    }

    private bool AcceptInstance(BrowserDebugBinding binding, JsonElement result)
    {
        if (
            result.TryGetProperty("_meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && String(meta, "sbx/browser_id") is { } id
            && String(meta, "sbx/browser_instance") is { } instance
        )
        {
            return _bindings.BindInstance(binding, id, instance);
        }
        _bindings.Revoke(binding);
        return false;
    }

    private static ToolHandlerResult Replaced(BrowserDebugBinding binding, JsonElement result) =>
        ToolHandlerResult.FromError(
            JsonSerializer.Serialize(
                new
                {
                    message = "The browser instance changed or its identity was missing. Old page evidence is invalid. Call OpenDebugBrowser again.",
                    browserHandle = binding.Handle,
                    previousBrowserInstance = binding.BrowserInstance,
                    gatewayMetadata = result.TryGetProperty("_meta", out var meta) ? meta : (JsonElement?)null,
                }
            ),
            "browser_replaced"
        );

    private static ToolHandlerResult Relay(BrowserDebugBinding binding, params JsonElement[] results)
    {
        var blocks = new List<ToolResultContentBlock>();
        var description = JsonSerializer.Serialize(
            new
            {
                browserHandle = binding.Handle,
                previewUrl = binding.PreviewUrl,
                browserInstance = binding.BrowserInstance,
                target = new
                {
                    miniAppId = binding.Target.App?.Id,
                    htmlFilePath = binding.Target.HtmlFilePath,
                    assetsRootPath = binding.Target.AssetsRootPath,
                },
                metadata = results.Select(r => r.TryGetProperty("_meta", out var meta) ? meta : (JsonElement?)null),
                structuredContent = results.Select(r =>
                    r.TryGetProperty("structuredContent", out var value) ? value : (JsonElement?)null
                ),
            }
        );
        blocks.Add(new TextToolResultBlock { Text = description });
        var text = new List<string> { description };
        foreach (var result in results)
        {
            if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var block in content.EnumerateArray())
            {
                if (
                    String(block, "type") == "image"
                    && String(block, "data") is { } data
                    && String(block, "mimeType") is { } mime
                )
                {
                    blocks.Add(new ImageToolResultBlock { Data = data, MimeType = mime });
                }
                else
                {
                    var value = String(block, "text") ?? block.GetRawText();
                    text.Add(value);
                    blocks.Add(new TextToolResultBlock { Text = value });
                }
            }
        }
        var error = results.Any(IsError);
        var summary = JsonNode.Parse(description)!.AsObject();
        summary["messages"] = JsonSerializer.SerializeToNode(text.Skip(1));
        return new ToolHandlerResult.Resolved(
            new(summary.ToJsonString(), blocks, error, error ? "browser_tool_error" : null)
        );
    }

    private static bool IsError(JsonElement result) =>
        result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException("Expected a non-empty string.");
        }
        return value.GetString();
    }

    private static string WorkspacePath(string path)
    {
        if (
            path.Length > 2048
            || path.StartsWith('/')
            || path.Contains('\\')
            || path.Contains('%')
            || path.Any(char.IsControl)
            || path.Split('/').Any(p => p is "" or "." or "..")
        )
        {
            throw new ArgumentException("Expected a workspace-relative path.");
        }
        return path;
    }

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private bool IsLive(BrowserDebugBinding binding) =>
        _bindings.TryGetOwned(binding.Handle, _threadId, _workspaceId, _sessionId, out var current)
        && ReferenceEquals(current, binding);

    /// <summary>
    /// Revokes the preview and releases the gateway lease. The binding leaves <c>_owned</c> only once
    /// the gateway confirms, so a failed release is retried by a later open, close or disposal.
    /// </summary>
    private async Task CleanupAsync(BrowserDebugBinding binding)
    {
        _bindings.Revoke(binding);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _gateway.CloseAsync(binding.BrowserId, timeout.Token);
            _owned.Remove(binding.Handle);
        }
        catch (Exception ex) when (ex is SandboxException or OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Debug browser {BrowserId} cleanup failed ({ErrorKind}); the lease is kept for a later retry",
                binding.BrowserId,
                (ex as SandboxException)?.Kind.ToString() ?? ex.GetType().Name
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _operations.WaitAsync();
        try
        {
            foreach (var binding in _owned.Values.ToArray())
            {
                await CleanupAsync(binding);
            }
            _owned.Clear();
            (_gateway as IDisposable)?.Dispose();
        }
        finally
        {
            _operations.Release();
        }
    }
}
