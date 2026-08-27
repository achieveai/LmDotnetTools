using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// A throwaway REAL git universe — a bare <c>origin</c> plus as many clones as a test asks for — driven
/// through the same <see cref="ISandboxCommandRunner"/> abstraction production uses (issue #471).
/// <para>
/// It exists for the handful of tests whose subject IS git behaviour. <see cref="FakeSandboxCommandRunner"/>
/// matches argv; it never merges anything, so it structurally cannot observe content loss — which is why the
/// derived-listing-loss proof for issue #218 item 6 had to be split into an argv-level ordering pin plus a
/// separate regenerator-content test, with no single test showing an entry surviving a merge while vanishing
/// from the listings. That end-to-end claim needs a merge that really happens. Everything else stays on the
/// argv matchers, which are faster and pin ORDER, which real git cannot.
/// </para>
/// </summary>
internal sealed class RealGitFixture : IDisposable
{
    private readonly HostGitCommandRunner _hostGit;

    private RealGitFixture(string root, HostGitCommandRunner hostGit)
    {
        Root = root;
        _hostGit = hostGit;
        Runner = new LocalRemoteRunner(hostGit);
    }

    /// <summary>The temp directory every repository in this fixture lives under.</summary>
    public string Root { get; }

    /// <summary>The bare repository the clones treat as <c>origin</c>.</summary>
    public string OriginPath => Path.Combine(Root, "origin.git");

    /// <summary>
    /// The runner to hand a <c>GitRunner</c>. Real processes, real working directories — and one relaxation,
    /// see <see cref="LocalRemoteRunner"/>.
    /// </summary>
    public ISandboxCommandRunner Runner { get; }

    /// <summary>
    /// Every git command the code under test issued through <see cref="Runner"/>, joined argv, in order.
    /// Real git reports outcomes but not which path produced them, and several of these tests are about a
    /// path (did the retry actually retry?) rather than only its result.
    /// </summary>
    public IReadOnlyList<string> Commands => ((LocalRemoteRunner)Runner).Recorded;

    /// <summary>Creates the temp root and initialises the bare <c>origin</c>.</summary>
    public static async Task<RealGitFixture> CreateAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "crd-realgit-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        var fixture = new RealGitFixture(
            root,
            new HostGitCommandRunner(
                _ => Task.FromResult<IReadOnlyList<GitProviderToken>>([]),
                NullLogger<HostGitCommandRunner>.Instance));

        await fixture.GitAsync(root, cancellationToken, "init", "--bare", "--initial-branch=main", "origin.git")
            .ConfigureAwait(false);
        return fixture;
    }

    /// <summary>Clones <c>origin</c> into <paramref name="name"/> under the root and returns its path.</summary>
    public async Task<string> CloneAsync(string name, CancellationToken cancellationToken = default)
    {
        // core.autocrlf=false because the listings this fixture is about are rendered with LF by
        // KnowledgeIndex/KnowledgeTableOfContents. On a machine whose global config turns autocrlf on, git
        // would hand every read-back listing CRLF line endings, the regenerator would compare LF content to
        // CRLF content and answer "changed" on every single merge — turning the assertions below into a
        // property of the developer's git config rather than of the code.
        await GitAsync(Root, cancellationToken, "-c", "core.autocrlf=false", "clone", OriginPath, name)
            .ConfigureAwait(false);
        var path = Path.Combine(Root, name);
        await GitAsync(path, cancellationToken, "config", "core.autocrlf", "false").ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Runs a git command in <paramref name="workingDirectory"/> with the review bot's identity, throwing on
    /// a non-zero exit so a broken fixture fails where it broke rather than as a mystifying assertion later.
    /// </summary>
    public async Task<string> GitAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] args)
    {
        string[] argv =
        [
            "git",
            "-c", "protocol.file.allow=always",
            "-c", "core.longpaths=true",
            "-c", "user.name=Fixture",
            "-c", "user.email=fixture@achieveai.local",
            .. args,
        ];
        var result = await _hostGit.RunAsync(new SandboxCommand(argv, workingDirectory), cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded
            ? result.Stdout
            : throw new InvalidOperationException(
                $"fixture git '{string.Join(' ', args)}' in '{workingDirectory}' exited {result.ExitCode}: "
                    + $"{result.Stderr}{result.Stdout}");
    }

    /// <summary>Writes a file under <paramref name="repoPath"/>, creating parent directories.</summary>
    public static void Write(string repoPath, string relativePath, string content)
    {
        var full = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // NewLine "\n" so the fixture writes exactly what the renderers write; see CloneAsync on autocrlf.
        File.WriteAllText(full, content.ReplaceLineEndings("\n"));
    }

    /// <summary>Reads a file under <paramref name="repoPath"/>, or <c>null</c> when it is not there.</summary>
    public static string? Read(string repoPath, string relativePath)
    {
        var full = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    public void Dispose() => ForceDelete(Root);

    /// <summary>
    /// Deletes a tree that contains a git repository. Git creates loose objects, packs and its index
    /// READ-ONLY, and <see cref="Directory.Delete(string, bool)"/> refuses those with
    /// <see cref="UnauthorizedAccessException"/> — so the ordinary <c>try { Delete } catch { }</c> around a
    /// temp repo does not clean up, it merely stops complaining, and every run leaks a repository into TEMP.
    /// Clear the attribute first, then delete; retry a couple of times for the brief window in which a git
    /// child process still holds a handle.
    /// </summary>
    public static void ForceDelete(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                ClearReadOnly(new DirectoryInfo(path));
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static void ClearReadOnly(DirectoryInfo directory)
    {
        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }

    /// <summary>
    /// The production <see cref="ISandboxCommandRunner"/>, with exactly one flag rewritten:
    /// <c>protocol.file.allow=never</c> becomes <c>always</c>.
    /// <para>
    /// <c>GitRunner</c> puts that hardening flag on EVERY invocation, and it bans the local transport — the
    /// only kind of remote a self-contained test can stand up. Without the rewrite <c>fetch</c> and
    /// <c>push</c> here fail "transport 'file' not allowed" and no merge path is reachable at all. The
    /// rewrite is by exact token so it cannot silently relax anything else: the ext-transport ban, the
    /// neutralized hooks path and the committer identity all reach real git untouched, and if the hardening
    /// flag is ever renamed this quietly stops applying rather than widening.
    /// </para>
    /// </summary>
    private sealed class LocalRemoteRunner(ISandboxCommandRunner inner) : ISandboxCommandRunner
    {
        private readonly List<string> _recorded = [];

        public IReadOnlyList<string> Recorded => _recorded;

        public Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
        {
            _recorded.Add(string.Join(' ', command.Argv));
            IReadOnlyList<string> argv =
            [
                .. command.Argv.Select(a =>
                    string.Equals(a, "protocol.file.allow=never", StringComparison.Ordinal)
                        ? "protocol.file.allow=always"
                        : a),
            ];
            return inner.RunAsync(new SandboxCommand(argv, command.WorkingDirectory), cancellationToken);
        }
    }
}
