using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>Shared bounded CGI execution for user launches and signed browser previews.</summary>
public static class SandboxCgiRunner
{
    private static readonly SemaphoreSlim Capacity = new(4, 4);

    public static async Task ExecuteAsync(
        HttpContext context,
        IWorkspaceFileBrowser browser,
        ILogger logger,
        SandboxAppDefinition app,
        string sessionId,
        string path,
        string pathPrefix,
        byte[] body,
        string csrfToken,
        Func<HttpContext, int, Task> failure
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(failure);
        if (!await Capacity.WaitAsync(0, context.RequestAborted))
        {
            await failure(context, StatusCodes.Status429TooManyRequests);
            return;
        }
        var cgi = new SandboxCgiResponse(context.Response, pathPrefix);
        try
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            executionCts.CancelAfter(app.ExecutionTimeout);
            var request = context.Request;
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["REQUEST_METHOD"] = request.Method,
                ["PATH_INFO"] = path,
                ["QUERY_STRING"] = request.QueryString.Value?.TrimStart('?') ?? "",
                ["CONTENT_TYPE"] = request.ContentType ?? "",
                ["CONTENT_LENGTH"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["HTTPS"] = "on",
                ["SERVER_NAME"] = request.Host.Host,
                ["SANDBOX_APP_CSRF_TOKEN"] = csrfToken,
            };
            var csrfHeader = request.Headers["X-CSRF-Token"].ToString();
            if (HttpMethods.IsPost(request.Method) && !string.IsNullOrEmpty(csrfHeader))
            {
                env["HTTP_X_CSRF_TOKEN"] = csrfHeader;
            }
            var command = new SandboxCommand([app.Executable, .. app.Arguments], app.WorkingDirectory);
            var result = await browser.ExecuteWorkspaceCommandStreamingAsync(
                sessionId,
                command,
                async (chunk, ct) =>
                {
                    if (chunk.Stream == SandboxOutputStream.Stdout)
                    {
                        await cgi.WriteAsync(chunk.Data, ct);
                    }
                    else if (!chunk.Data.IsEmpty)
                    {
                        logger.LogWarning("Sandbox app {AppId} wrote {Bytes} stderr bytes", app.Id, chunk.Data.Length);
                    }
                },
                body,
                env,
                app.MaxOutputBytes,
                executionCts.Token
            );
            await cgi.CompleteAsync(executionCts.Token);
            if (result.ExitCode != 0)
            {
                logger.LogWarning("Sandbox app {AppId} exited with code {ExitCode}", app.Id, result.ExitCode);
                await FailAsync(context, cgi, StatusCodes.Status502BadGateway, failure);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The SDK cancels the gateway operation when the browser disconnects.
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Sandbox app {AppId} exceeded its execution time limit", app.Id);
            await FailAsync(context, cgi, StatusCodes.Status504GatewayTimeout, failure);
        }
        catch (Exception ex) when (ex is SandboxException or SandboxSessionUnavailableException or InvalidDataException)
        {
            logger.LogWarning(ex, "Sandbox app {AppId} failed", app.Id);
            await FailAsync(context, cgi, StatusCodes.Status502BadGateway, failure);
        }
        finally
        {
            Capacity.Release();
        }
    }

    private static async Task FailAsync(
        HttpContext context,
        SandboxCgiResponse cgi,
        int status,
        Func<HttpContext, int, Task> failure
    )
    {
        if (context.Response.HasStarted || cgi.HasWrittenBody)
        {
            context.Abort();
            return;
        }
        context.Response.Headers.Remove("Location");
        await failure(context, status);
    }
}
