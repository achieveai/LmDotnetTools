namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Reads and writes files inside the sandbox working tree. This is a deliberately tiny companion to
/// <see cref="ISandboxCommandRunner"/>: the daemon writes review artifacts (PRs/, KnowledgeBase/) into
/// the ReviewBot checkout before committing, and reads <c>.gitmodules</c> while walking submodules.
/// Keeping it an interface lets the deterministic orchestration (<c>ReviewBotRepoManager</c>,
/// <c>SubmoduleInitializer</c>) be verified against an in-memory fake with no live gateway.
/// </summary>
internal interface ISandboxFileSystem
{
    /// <summary>Reads a UTF-8 text file, or returns <c>null</c> when it does not exist.</summary>
    Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes UTF-8 text, creating parent directories as needed (overwrites if present).</summary>
    Task WriteFileAsync(string path, string content, CancellationToken cancellationToken);
}
