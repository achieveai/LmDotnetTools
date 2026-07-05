using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The daemon's HOST-side write surface for ReviewBot retention (design §6). Bundles the host git runner
/// + filesystem + the host path the ReviewBot store is cloned to, so all retention writes happen in the
/// daemon process with the write credential — never in the read-only sandbox the review agent shares.
/// </summary>
internal sealed record HostRetentionWorkspace(
    ISandboxCommandRunner Git,
    ISandboxFileSystem FileSystem,
    string RepoRoot);
