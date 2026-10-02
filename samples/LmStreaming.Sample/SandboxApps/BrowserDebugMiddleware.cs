using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.FileBrowser;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>Terminates gateway browser traffic before identity, chat routes, static files or SPA fallback.</summary>
public sealed class BrowserDebugMiddleware(
    RequestDelegate next,
    BrowserDebugBindingStore bindings,
    BrowserDebugAccess access,
    IWorkspaceFileBrowser files,
    SandboxAppCatalog catalog,
    ILogger<BrowserDebugMiddleware> logger
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        var browserRequest =
            request.Path.StartsWithSegments("/_debug-browser")
            || request.Headers.ContainsKey("X-Sbx-Browser-Id")
            || request.Headers.ContainsKey("X-Sbx-Browser-Instance")
            || request.Headers.ContainsKey("X-Sbx-Browser-Signature")
            || request.Headers.ContainsKey("X-Sbx-Original-Url");
        if (!browserRequest)
        {
            await next(context);
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!bindings.TryValidate(request, out var binding, out var path))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var resolved = await access.ResolveAsync(binding!.ThreadId, binding.Principal, null, context.RequestAborted);
        if (
            resolved.Status != 200
            || resolved.SessionId != binding.SessionId
            || resolved.WorkspaceId != binding.WorkspaceId
        )
        {
            bindings.Revoke(binding);
            context.Response.StatusCode = resolved.Status == 200 ? StatusCodes.Status403Forbidden : resolved.Status;
            return;
        }
        try
        {
            if (binding.Target.App is { } app)
            {
                await ServeAppAsync(context, binding, app, path!);
            }
            else
            {
                await ServeHtmlAsync(context, binding, path!);
            }
        }
        catch (SandboxException ex) when (ex.IsDirectReadCapExceeded)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (Exception ex)
            when (ex
                    is SandboxException
                        or SandboxSessionUnavailableException
                        or InvalidDataException
                        or NotSupportedException
            )
        {
            logger.LogWarning(
                ex,
                "Browser preview of {BrowserId} failed: {ErrorKind}",
                binding.BrowserId,
                (ex as SandboxException)?.Kind.ToString() ?? ex.GetType().Name
            );
            if (context.Response.HasStarted)
            {
                context.Abort();
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    private async Task ServeHtmlAsync(HttpContext context, BrowserDebugBinding binding, string path)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        var root = binding.Target.AssetsRootPath ?? "";
        var workspacePath = root.Length == 0 ? path : root + "/" + path;
        if (FilePreviewPolicy.IsUnderDotDirectory(workspacePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var file = await WorkspacePathResolver.ResolveAsync(
            files,
            binding.SessionId,
            workspacePath,
            context.RequestAborted
        );
        if (!file.Success || file.Type != SandboxEntryType.File)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (file.Size is > FileBrowserLimits.MaxDownloadBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        var (status, bytes) = await WorkspaceAssetReader.ReadAsync(
            files,
            binding.SessionId,
            root,
            path,
            FileBrowserLimits.MaxDownloadBytes,
            context.RequestAborted
        );
        if (status != 200)
        {
            context.Response.StatusCode = status;
            return;
        }
        if (bytes.LongLength > FileBrowserLimits.MaxDownloadBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        var fileName = file.ServerPath.Split('/')[^1];
        var contentType = WorkspaceContentTypes.ForFileName(fileName);
        WorkspaceContentTypes.ApplyHeaders(context.Response, contentType, fileName);
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.LongLength;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
    }

    private async Task ServeAppAsync(
        HttpContext context,
        BrowserDebugBinding binding,
        SandboxAppDefinition app,
        string path
    )
    {
        if (!catalog.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsPost(request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        if (request.ContentType is { Length: > 128 })
        {
            context.Response.StatusCode = StatusCodes.Status414UriTooLong;
            return;
        }
        byte[] body;
        try
        {
            body = await SandboxAppMiddleware.ReadBodyAsync(request, app.MaxRequestBytes, context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (HttpMethods.IsPost(request.Method) && !SandboxAppMiddleware.ValidCsrf(request, body, binding.CsrfToken))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        SandboxAppMiddleware.SetSecurityHeaders(
            context.Response,
            catalog.SiteDomain,
            catalog.HttpsPort,
            sameOrigin: true
        );
        // CGI sees the preview origin; request equality and the signed original URL were checked above.
        request.Host = new HostString(new Uri(binding.PreviewUrl).Host, new Uri(binding.PreviewUrl).Port);
        await SandboxCgiRunner.ExecuteAsync(
            context,
            files,
            logger,
            app,
            binding.SessionId,
            "/" + path,
            binding.Prefix,
            body,
            binding.CsrfToken,
            (ctx, status) =>
            {
                ctx.Response.StatusCode = status;
                return Task.CompletedTask;
            }
        );
    }
}
