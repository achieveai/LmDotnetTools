using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using CodeReviewDaemon.Sample.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Reviewd.Sample;

internal static class AuthCommands
{
    public static async Task<int> RunAsync(
        CliOptions options,
        ReviewdConfig config,
        CancellationToken ct,
        Func<CancellationToken, Task>? work = null
    )
    {
        var auth = LoadOptions(config, options.Get("callback"));
        var listen = options.Get("listen") ?? "http://127.0.0.1:5203";
        _ = ValidateUrl(listen, listenAddress: true);
        var builder = WebApplication.CreateSlimBuilder([]);
        builder.WebHost.UseUrls(listen);
        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton<IOAuthTokenStore>(sp => new FileOAuthTokenStore(
            auth.TokenStoreDir!,
            sp.GetRequiredService<ILogger<FileOAuthTokenStore>>()
        ));
        builder.Services.AddSingleton(sp => new SessionSecretStore(
            Path.Combine(auth.TokenStoreDir!, "session-secrets"),
            sp.GetRequiredService<ILogger<SessionSecretStore>>()
        ));
        if (!string.IsNullOrWhiteSpace(auth.Github.ClientId))
        {
            builder.Services.AddSingleton(sp => new GitHubOAuthProvider(
                auth.Github,
                sp.GetRequiredService<IOAuthTokenStore>(),
                new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }),
                sp.GetRequiredService<ILogger<GitHubOAuthProvider>>()
            ));
            builder.Services.AddSingleton<IOAuthTokenProvider>(sp => sp.GetRequiredService<GitHubOAuthProvider>());
        }
        if (!string.IsNullOrWhiteSpace(auth.Ado.ClientId))
        {
            builder.Services.AddSingleton(sp => new AdoOAuthProvider(
                auth.Ado,
                Path.Combine(auth.TokenStoreDir!, "msal-ado.bin"),
                sp.GetRequiredService<ILogger<AdoOAuthProvider>>()
            ));
            builder.Services.AddSingleton<IOAuthTokenProvider>(sp => sp.GetRequiredService<AdoOAuthProvider>());
        }
        builder.Services.AddSingleton<IAuthEventNotifier, DaemonAuthEventNotifier>();
        builder.Services.AddSingleton<IAuthResolutionPolicy, FailFastDaemonAuthPolicy>();
        builder.Services.AddSingleton<IAuthWebhookForwarder, NoOpAuthWebhookForwarder>();
        builder
            .Services.AddControllers()
            .ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(AuthWebhookController).Assembly));
                manager.FeatureProviders.Clear();
                manager.FeatureProviders.Add(new WebhookControllerFeatureProvider());
            });
        if (work is not null)
            builder.Services.AddSingleton<IHostLifetime, BatchHostLifetime>();
        await using var app = builder.Build();
        app.MapControllers();
        Console.Error.WriteLine(
            $"OAuth webhook only; callback {auth.Webhook.CallbackBaseUrl}. No daemon workers or sign-in routes."
        );
        await app.StartAsync(ct);
        try
        {
            if (work is not null)
            {
                try
                {
                    await work(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Scheduling cancelled; admitted work drained before stopping OAuth.");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"Batch stopped: {ex.Message}. OAuth callback remains running; no replay.");
                }
                Console.Error.WriteLine(
                    "Scheduling stopped. OAuth callback remains running until explicit process shutdown."
                );
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                return 0;
            }
            if (options.Command == "auth run")
            {
                var start = new ProcessStartInfo(options.Argv[0]) { UseShellExecute = false };
                foreach (var argument in options.Argv.Skip(1))
                {
                    start.ArgumentList.Add(argument);
                }
                using var process =
                    Process.Start(start) ?? throw new InvalidOperationException("Could not start task command.");
                try
                {
                    await process.WaitForExitAsync(ct);
                    return process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                    throw;
                }
            }
            await app.WaitForShutdownAsync(ct);
            return 0;
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    internal static AuthOptions LoadOptions(ReviewdConfig config, string? callback)
    {
        var auth =
            config.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
            ?? throw new InvalidOperationException("Configure Auth and the existing shared token store.");
        if (
            string.IsNullOrWhiteSpace(auth.TokenStoreDir)
            || !Path.IsPathFullyQualified(auth.TokenStoreDir)
            || !Directory.Exists(auth.TokenStoreDir)
        )
        {
            throw new InvalidOperationException(
                "Auth:TokenStoreDir must name the existing absolute shared OAuth cache directory."
            );
        }
        if (string.IsNullOrWhiteSpace(auth.Ado.ClientId) && string.IsNullOrWhiteSpace(auth.Github.ClientId))
        {
            throw new InvalidOperationException("Configure at least one existing ADO or GitHub OAuth provider.");
        }
        if (
            !string.IsNullOrWhiteSpace(auth.Ado.ClientId)
            && !File.Exists(Path.Combine(auth.TokenStoreDir!, "msal-ado.bin"))
        )
        {
            throw new InvalidOperationException(
                "Shared ADO OAuth cache missing; sign in through the existing host first."
            );
        }
        auth.Webhook.PublicBaseUrl = (callback ?? "http://localhost:5203").TrimEnd('/');
        _ = ValidateUrl(auth.Webhook.PublicBaseUrl);
        return auth;
    }

    private static Uri ValidateUrl(string value, bool listenAddress = false)
    {
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/"
        )
        {
            throw new ArgumentException(
                "Webhook URLs must be HTTP(S) origins without credentials, path, query, or fragment."
            );
        }
        var localAddress =
            listenAddress
            && IPAddress.TryParse(uri.Host, out var address)
            && NetworkInterface
                .GetAllNetworkInterfaces()
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address));
        if (listenAddress && !uri.IsLoopback && !localAddress)
        {
            throw new ArgumentException(
                "Listen URL must bind a loopback or explicitly assigned local interface address."
            );
        }
        if (uri.Scheme == "http" && !uri.IsLoopback && uri.Host != "host.docker.internal" && !localAddress)
        {
            throw new ArgumentException(
                "Webhook HTTP is restricted to loopback or host.docker.internal; use HTTPS remotely."
            );
        }
        return uri;
    }

    private sealed class BatchHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class WebhookControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(AuthWebhookController);
    }
}
