namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// Builds the <see cref="OperationPolicy"/> the daemon's outbound HTTP seam enforces (Thread #1 / plan
/// §4). Over HTTP only the provider-API operations are classified — <see cref="SandboxOperation.PostReviewComment"/>
/// and <see cref="SandboxOperation.ReadProviderMetadata"/> — whose decision depends on the provider's
/// API host and the request method, so the API host is the load-bearing field here. The git
/// transport fields (<c>TargetHost</c>/<c>ReviewBotHost</c>/fork/submodules) are not reachable through
/// the HTTP handler; they are populated with the provider's git host and left without a specific repo
/// path, since git fetch/push enforcement is performed at the sandbox/git seam, not this one.
/// </summary>
internal static class DaemonOperationPolicy
{
    /// <summary>The primary (write-capable) policy for GitHub provider-API requests.</summary>
    public static OperationPolicy ForGitHub() =>
        new(
            new ReviewScope(
                Provider: "github",
                TargetHost: "github.com",
                TargetRepoPath: "/",
                ForkHost: null,
                ForkRepoPath: null,
                ReviewBotHost: "github.com",
                ReviewBotRepoPath: "/",
                ApiHost: "api.github.com",
                AllowedSubmodules: []),
            allowWriteOperations: true);

    /// <summary>The primary (write-capable) policy for Azure DevOps provider-API requests.</summary>
    public static OperationPolicy ForAdo() =>
        new(
            new ReviewScope(
                Provider: "ado",
                TargetHost: "dev.azure.com",
                TargetRepoPath: "/",
                ForkHost: null,
                ForkRepoPath: null,
                ReviewBotHost: "dev.azure.com",
                ReviewBotRepoPath: "/",
                ApiHost: "dev.azure.com",
                AllowedSubmodules: []),
            allowWriteOperations: true);
}
