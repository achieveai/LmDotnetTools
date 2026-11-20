using System.Security.Cryptography;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Reviewd.Sample;

internal static class SandboxCommands
{
    public static async Task<int> RunAsync(CliOptions options, ReviewdConfig config, CancellationToken ct)
    {
        // For exec, --timeout is also the gateway's execution deadline, so a long command is not cut at 2 minutes.
        var timeout =
            options.Command == "sandbox exec"
                ? int.Parse(options.Get("timeout") ?? "120", System.Globalization.CultureInfo.InvariantCulture)
                : 120;
        if (timeout is < 1 or > 86400)
        {
            throw new ArgumentException("--timeout must be between 1 and 86400 seconds.");
        }
        using var sandbox = config.CreateSandboxClient(TimeSpan.FromSeconds(timeout));
        if (options.Command == "sandbox create")
        {
            return await CreateAsync(options, config, sandbox, ResolveReadPathsAsync, Console.Out, Console.Error, ct);
        }
        if (options.Command != "sandbox exec")
        {
            throw new ArgumentException("Unsupported sandbox command.");
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

    /// <summary>
    ///     <c>sandbox create</c>: builds the session's egress policy (read-only with <c>--read-only-egress</c>), creates
    ///     the session, keeps its webhook secret and, for a read-only session, writes its marker.
    /// </summary>
    internal static async Task<int> CreateAsync(
        CliOptions options,
        ReviewdConfig config,
        SandboxClient sandbox,
        Func<AuthOptions, IReadOnlyList<RepoIdentity>, CancellationToken, Task<IReadOnlyList<string>>> readPathsOf,
        TextWriter output,
        TextWriter error,
        CancellationToken ct
    )
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
        // Read-only egress narrows every Azure DevOps rule to GET and HEAD (a shadow review's --post-session),
        // plus POST to each listed repo's git fetch and prepare's two read queries, so prepare runs there too.
        var readOnly = options.Has("read-only-egress");
        var readRepos = (options.Get("read-repos") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(PrCommands.ParseRepo)
            .ToArray();
        if (readRepos.Any(repo => repo.Provider != "azure-devops"))
        {
            throw new ArgumentException("--read-repos takes Azure DevOps repositories (org/project/repo) only.");
        }
        var readPaths = readRepos.Length > 0 ? await readPathsOf(auth, readRepos, ct) : [];
        var policy = SandboxSessionRegistry.BuildAuthPolicy(
            auth,
            gateway,
            secret,
            readOnlyEgress: readOnly,
            readOnlyAdoPostPaths: [.. readPaths.Distinct(StringComparer.Ordinal)]
        );
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
            error.WriteLine(
                $"Session {session.SessionId} was created but webhook-secret persistence failed. No command was executed; the session was not deleted."
            );
            throw;
        }
        // The gateway does not report a session's rules back, so the host keeps the read-only mark itself; a
        // review run --shadow refuses a --post-session without it.
        if (readOnly)
        {
            await MarkCreatedSessionAsync(
                auth.TokenStoreDir!,
                session.SessionId,
                options.Required("workspace"),
                [.. readRepos.Select(repo => repo.DisplayName)],
                error,
                ct
            );
        }
        output.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    sessionId = session.SessionId,
                    appId = config.AppId,
                    workspace = options.Required("workspace"),
                    callback = auth.Webhook.CallbackBaseUrl,
                    readOnlyEgress = readOnly,
                }
            )
        );
        return 0;
    }

    /// <summary>The exact POST paths a read-only session needs for each <c>--read-repos</c> repository.</summary>
    private static async Task<IReadOnlyList<string>> ResolveReadPathsAsync(
        AuthOptions auth,
        IReadOnlyList<RepoIdentity> repos,
        CancellationToken ct
    )
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var tokens = new AdoOAuthProvider(
            auth.Ado,
            Path.Combine(auth.TokenStoreDir!, "msal-ado.bin"),
            NullLogger<AdoOAuthProvider>.Instance
        );
        var paths = new List<string>();
        foreach (var repo in repos)
        {
            paths.AddRange(await AdoReadOnlyPaths.ResolveAsync(http, tokens, repo, ct));
        }
        return paths;
    }

    /// <summary>
    ///     Writes a just-created read-only session's marker. The session already exists, so a failure names it and says
    ///     it was kept before rethrowing: without the marker no shadow run will accept it.
    /// </summary>
    internal static async Task MarkCreatedSessionAsync(
        string tokenStoreDir,
        string sessionId,
        string workspace,
        IReadOnlyList<string> readRepos,
        TextWriter error,
        CancellationToken ct
    )
    {
        try
        {
            await ReadOnlySessions.MarkAsync(tokenStoreDir, sessionId, workspace, readRepos, ct);
        }
        catch
        {
            await error.WriteLineAsync(
                $"Session {sessionId} was created with read-only egress but its read-only marker could not be written, so no shadow run will accept it. No command was executed; the session was not deleted."
            );
            throw;
        }
    }
}
