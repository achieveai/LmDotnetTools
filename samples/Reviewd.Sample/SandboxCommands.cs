using System.Security.Cryptography;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Reviewd.Sample;

internal static class SandboxCommands
{
    public static async Task<int> RunAsync(CliOptions options, ReviewdConfig config, CancellationToken ct)
    {
        using var sandbox = config.CreateSandboxClient();
        if (options.Command == "sandbox create")
        {
            var auth = AuthCommands.LoadOptions(config, options.Required("callback"));
            var gateway =
                config.Configuration.GetSection("SandboxGateway").Get<SandboxGatewayOptions>()
                ?? new SandboxGatewayOptions();
            if (!string.IsNullOrWhiteSpace(auth.M365.ClientId) || gateway.AuthProviders is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    "Reviewd's webhook supports managed ADO/GitHub only; unsupported auth providers require the existing host."
                );
            }
            var secret = RandomNumberGenerator.GetHexString(64);
            var policy = SandboxSessionRegistry.BuildAuthPolicy(auth, gateway, secret);
            var marketplaces = (options.Get("marketplaces") ?? "").Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            );
            var session = await sandbox.CreateAsync(
                new SandboxCreateRequest(options.Required("workspace"), marketplaces, policy.Providers, policy.Network),
                ct
            );
            var secrets = new SessionSecretStore(
                Path.Combine(auth.TokenStoreDir!, "session-secrets"),
                NullLogger<SessionSecretStore>.Instance
            );
            try
            {
                await secrets.SaveAsync(session.SessionId, secret, ct);
            }
            catch
            {
                Console.Error.WriteLine(
                    $"Session {session.SessionId} was created but webhook-secret persistence failed. No command was executed; the session was not deleted."
                );
                throw;
            }
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        sessionId = session.SessionId,
                        appId = config.AppId,
                        workspace = options.Required("workspace"),
                        callback = auth.Webhook.CallbackBaseUrl,
                    }
                )
            );
            return 0;
        }
        if (options.Command != "sandbox exec")
        {
            throw new ArgumentException("Unsupported sandbox command.");
        }
        var timeout = int.Parse(options.Get("timeout") ?? "120", System.Globalization.CultureInfo.InvariantCulture);
        if (timeout is < 1 or > 86400)
        {
            throw new ArgumentException("--timeout must be between 1 and 86400 seconds.");
        }
        var operation = Guid.NewGuid().ToString("N");
        var command = new SandboxCommand(options.Argv, options.Get("directory") ?? ".", operationId: operation);
        Console.Error.WriteLine(
            $"Session {options.Required("session")}; operation {operation}. Local cancellation may leave remote work running."
        );
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(timeout));
        var result = await sandbox.ExecuteAsync(options.Required("session"), command, budget.Token);
        Console.Out.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
        return result.ExitCode;
    }
}
