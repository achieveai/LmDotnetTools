using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.AspNetCore.WebUtilities;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>Owns every request to the dedicated app domain before static files or SPA fallback run.</summary>
public sealed class SandboxAppMiddleware(
    RequestDelegate next,
    SandboxAppCatalog catalog,
    SandboxAppInstanceStore instances,
    SandboxAppAccess access,
    IWorkspaceFileBrowser browser,
    Identity.ConversationAuthorizer authorizer,
    ILogger<SandboxAppMiddleware> logger
)
{
    private const string CookieName = "__Host-sandbox-app";
    private static readonly SemaphoreSlim Capacity = new(4, 4);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (!catalog.IsAppHost(request.Host.Host))
        {
            await next(context);
            return;
        }

        SetSecurityHeaders(context.Response, catalog.SiteDomain, catalog.HttpsPort);

        // This branch never falls through to chat APIs, static files, Vite or the SPA.
        if (!catalog.Enabled || !authorizer.IsEnforced || !request.IsHttps)
        {
            if (request.Path == "/_launch")
                await WriteFailurePageAsync(context, StatusCodes.Status404NotFound);
            else
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (request.Path == "/_launch")
        {
            await ExchangeAsync(context);
            return;
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsPost(request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var grant = instances.Authenticate(request.Cookies[CookieName], request.Host.Host);
        if (grant is null)
        {
            await FailInitialPageAsync(context, StatusCodes.Status401Unauthorized);
            return;
        }
        var app = grant.App;

        if (HttpMethods.IsGet(request.Method) && !IsSameSiteRead(request, catalog.SiteDomain))
        {
            await FailInitialPageAsync(context, StatusCodes.Status403Forbidden);
            return;
        }

        if (
            request.Path.Value is not { Length: <= 2048 } path
            || request.QueryString.Value is not { Length: <= 2048 } query
            || request.ContentType is { Length: > 128 }
        )
        {
            await FailInitialPageAsync(context, StatusCodes.Status414UriTooLong);
            return;
        }

        byte[] body;
        try
        {
            body = await ReadBodyAsync(request, app.MaxRequestBytes, context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            await FailInitialPageAsync(context, StatusCodes.Status413PayloadTooLarge);
            return;
        }

        if (HttpMethods.IsPost(request.Method) && !ValidCsrf(request, body, grant.CsrfToken))
        {
            await FailInitialPageAsync(context, StatusCodes.Status403Forbidden);
            return;
        }

        var resolved = await access.ResolveContextAsync(grant.ThreadId, grant.Principal, context.RequestAborted);
        if (resolved.Status != 200 || resolved.SessionId is null || resolved.WorkspaceId != grant.WorkspaceId)
        {
            await FailInitialPageAsync(
                context,
                resolved.Status != 200 ? resolved.Status : StatusCodes.Status403Forbidden
            );
            return;
        }

        if (!await Capacity.WaitAsync(0, context.RequestAborted))
        {
            await FailInitialPageAsync(context, StatusCodes.Status429TooManyRequests);
            return;
        }

        var cgi = new SandboxCgiResponse(context.Response);
        try
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            executionCts.CancelAfter(app.ExecutionTimeout);
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["REQUEST_METHOD"] = request.Method,
                ["PATH_INFO"] = path,
                ["QUERY_STRING"] = query.TrimStart('?'),
                ["CONTENT_TYPE"] = request.ContentType ?? string.Empty,
                ["CONTENT_LENGTH"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["HTTPS"] = "on",
                ["SERVER_NAME"] = request.Host.Host,
                ["SANDBOX_APP_CSRF_TOKEN"] = grant.CsrfToken,
            };
            var csrfHeader = request.Headers["X-CSRF-Token"].ToString();
            if (HttpMethods.IsPost(request.Method) && !string.IsNullOrEmpty(csrfHeader))
            {
                env["HTTP_X_CSRF_TOKEN"] = csrfHeader;
            }
            var args = new[] { app.Executable }.Concat(app.Arguments).ToArray();
            var command = new SandboxCommand(args, app.WorkingDirectory);
            var result = await browser.ExecuteWorkspaceCommandStreamingAsync(
                resolved.SessionId,
                command,
                async (chunk, ct) =>
                {
                    if (chunk.Stream == SandboxOutputStream.Stdout)
                        await cgi.WriteAsync(chunk.Data, ct);
                    else if (!chunk.Data.IsEmpty)
                        logger.LogWarning("Sandbox app {AppId} wrote {Bytes} stderr bytes", app.Id, chunk.Data.Length);
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
                await FailCgiAsync(context, cgi, StatusCodes.Status502BadGateway);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The SDK cancels the gateway operation when the browser disconnects.
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Sandbox app {AppId} exceeded its execution time limit", app.Id);
            await FailCgiAsync(context, cgi, StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex) when (ex is SandboxException or SandboxSessionUnavailableException or InvalidDataException)
        {
            logger.LogWarning(ex, "Sandbox app {AppId} failed", app.Id);
            await FailCgiAsync(context, cgi, StatusCodes.Status502BadGateway);
        }
        finally
        {
            Capacity.Release();
        }
    }

    private async Task ExchangeAsync(HttpContext context)
    {
        var request = context.Request;
        if (
            !HttpMethods.IsPost(request.Method)
            || request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                != true
        )
        {
            await WriteFailurePageAsync(context, StatusCodes.Status404NotFound);
            return;
        }
        byte[] body;
        try
        {
            body = await ReadBodyAsync(request, 4096, context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            await WriteFailurePageAsync(context, StatusCodes.Status413PayloadTooLarge);
            return;
        }
        var form = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(body));
        if (!form.TryGetValue("ticket", out var submittedTicket) || submittedTicket.Count != 1)
        {
            await WriteFailurePageAsync(context, StatusCodes.Status403Forbidden);
            return;
        }
        var grant = instances.Exchange(submittedTicket.ToString(), request.Host.Host);
        if (grant is null)
        {
            await WriteFailurePageAsync(context, StatusCodes.Status403Forbidden);
            return;
        }
        // Recheck authorization at exchange, before setting a credential on the app origin.
        var resolved = await access.ResolveContextAsync(grant.ThreadId, grant.Principal, context.RequestAborted);
        if (resolved.Status != 200 || resolved.WorkspaceId != grant.WorkspaceId)
        {
            await WriteFailurePageAsync(
                context,
                resolved.Status != 200 ? resolved.Status : StatusCodes.Status403Forbidden
            );
            return;
        }
        context.Response.Cookies.Append(
            CookieName,
            grant.Cookie,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = grant.ExpiresAt,
            }
        );
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = "/";
    }

    private static async Task FailCgiAsync(HttpContext context, SandboxCgiResponse cgi, int status)
    {
        if (context.Response.HasStarted || cgi.HasWrittenBody)
        {
            context.Abort();
            return;
        }
        context.Response.Headers.Remove("Location");
        await FailInitialPageAsync(context, status);
    }

    private static Task FailInitialPageAsync(HttpContext context, int status)
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/")
            return WriteFailurePageAsync(context, status);
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    private static Task WriteFailurePageAsync(HttpContext context, int status)
    {
        const string html =
            "<!doctype html><meta charset=\"utf-8\"><title>App unavailable</title>"
            + "<p>App unavailable. Retry from the workspace tab.</p>"
            + "<script>window.parent.postMessage({type:'sandbox-app-failed'}, '*')</script>";
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(html, context.RequestAborted);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, int cap, CancellationToken ct)
    {
        if (request.ContentLength > cap)
            throw new InvalidDataException("Request body exceeds limit.");
        await using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, ct);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > cap)
                throw new InvalidDataException("Request body exceeds limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static bool ValidCsrf(HttpRequest request, byte[] body, string expected)
    {
        var supplied = request.Headers["X-CSRF-Token"].ToString();
        if (
            string.IsNullOrEmpty(supplied)
            && request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                == true
        )
        {
            var form = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(body));
            if (form.TryGetValue("_csrf", out var formToken) && formToken.Count == 1)
                supplied = formToken.ToString();
        }
        return supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(supplied),
                Encoding.ASCII.GetBytes(expected)
            );
    }

    private static bool IsSameSiteRead(HttpRequest request, string siteDomain)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (!string.IsNullOrEmpty(fetchSite))
            return fetchSite is "same-origin" or "same-site" or "none";
        var origin = request.Headers.Origin.ToString();
        var referer = request.Headers.Referer.ToString();
        var candidate = string.IsNullOrEmpty(origin) ? referer : origin;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var source)
            && source.Scheme == Uri.UriSchemeHttps
            && (
                source.Host.Equals(siteDomain, StringComparison.OrdinalIgnoreCase)
                || source.Host.EndsWith("." + siteDomain, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static void SetSecurityHeaders(HttpResponse response, string siteDomain, int httpsPort)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers["Referrer-Policy"] = "no-referrer";
        var sitePort = httpsPort == 443 ? string.Empty : $":{httpsPort}";
        response.Headers.ContentSecurityPolicy =
            $"default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; "
            + $"img-src 'self' data:; connect-src 'self'; form-action 'self'; base-uri 'none'; "
            + $"frame-ancestors https://*.{siteDomain}{sitePort}";
    }
}
