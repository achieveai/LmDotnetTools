using System.Text;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Builds scoped knowledge replacements and their derived indexes from validated extraction values.</summary>
internal sealed class WorkflowKnowledgeEdits(
    string repoRoot,
    RepoIdentity repo,
    ISandboxFileSystem fileSystem,
    ILogger logger
)
{
    public async Task<IReadOnlyList<ReviewArtifactFile>> PrepareAsync(
        JsonArray extractions,
        CancellationToken cancellationToken
    )
    {
        var files = new List<ReviewArtifactFile>();
        var metadata = new List<KnowledgeEntryMeta>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var extraction in extractions)
        {
            if (extraction?["Edits"] is not JsonArray edits)
                throw new ArgumentException("Extraction must contain an Edits array.", nameof(extractions));
            foreach (var edit in edits)
            {
                var path =
                    edit?["Path"]?.GetValue<string>()
                    ?? throw new ArgumentException("Knowledge edit path is required.", nameof(extractions));
                var content =
                    edit?["Content"]?.GetValue<string>()
                    ?? throw new ArgumentException("Knowledge edit content is required.", nameof(extractions));
                ValidatePath(path, repo, allowListings: false);
                _ = WorkflowScriptInvoker.ResolveWorkspaceAsset(path, repoRoot);
                path = await PreserveExistingCaseAsync(path, cancellationToken).ConfigureAwait(false);
                _ = WorkflowScriptInvoker.ResolveWorkspaceAsset(path, repoRoot);
                if (!paths.Add(path))
                    throw new ArgumentException(
                        "Knowledge edit paths must be unique across extraction outputs.",
                        nameof(extractions)
                    );
                var bytes = Encoding.UTF8.GetByteCount(content);
                totalBytes += bytes;
                if (
                    bytes > SandboxReadLimits.KnowledgeEntryBytes
                    || totalBytes > SandboxReadLimits.KnowledgeListingBytes
                )
                    throw new ArgumentException(
                        "Knowledge edits exceed the bounded content limit.",
                        nameof(extractions)
                    );
                var relative = path["KnowledgeBase/".Length..];
                var meta = KnowledgeIndex.ParseFrontmatter(relative, content);
                if (
                    meta is null
                    || string.IsNullOrWhiteSpace(meta.Title)
                    || !string.Equals(meta.Scope, relative.Split('/')[0], StringComparison.OrdinalIgnoreCase)
                )
                    throw new ArgumentException(
                        "Knowledge content requires valid frontmatter with a title and matching scope.",
                        nameof(extractions)
                    );
                files.Add(new ReviewArtifactFile(path, content));
                metadata.Add(meta);
            }
        }
        if (files.Count == 0)
            return files;
        var contained = new ContainedFileSystem(repoRoot, fileSystem);
        var knowledgeRoot = repoRoot.TrimEnd('/', '\\') + "/KnowledgeBase";
        var (index, toc) = await new KnowledgeIndexRegenerator(contained, logger)
            .RenderListingsAsync(knowledgeRoot, metadata, cancellationToken)
            .ConfigureAwait(false);
        foreach (
            var listing in new[]
            {
                new ReviewArtifactFile("KnowledgeBase/_index.jsonl", index),
                new ReviewArtifactFile("KnowledgeBase/_toc.md", toc),
            }
        )
        {
            _ = WorkflowScriptInvoker.ResolveWorkspaceAsset(listing.RelativePath, repoRoot);
            if (Encoding.UTF8.GetByteCount(listing.Content) > SandboxReadLimits.KnowledgeListingBytes)
                throw new InvalidOperationException("Regenerated knowledge listing exceeds its size limit.");
            files.Add(listing);
        }
        return files;
    }

    private async Task<string> PreserveExistingCaseAsync(string path, CancellationToken cancellationToken)
    {
        var segments = path.Split('/');
        var root = repoRoot.TrimEnd('/', '\\') + "/KnowledgeBase";
        var contained = new ContainedFileSystem(repoRoot, fileSystem);
        var scopes = await contained.ListFilesAsync(root, cancellationToken).ConfigureAwait(false);
        segments[1] =
            scopes.FirstOrDefault(scope => string.Equals(scope, segments[1], StringComparison.OrdinalIgnoreCase))
            ?? segments[1];
        var names = await contained.ListFilesAsync(root + "/" + segments[1], cancellationToken).ConfigureAwait(false);
        segments[2] =
            names.FirstOrDefault(name => string.Equals(name, segments[2], StringComparison.OrdinalIgnoreCase))
            ?? segments[2];
        return string.Join('/', segments);
    }

    internal static void ValidatePath(string path, RepoIdentity repo, bool allowListings)
    {
        if (allowListings && path is "KnowledgeBase/_index.jsonl" or "KnowledgeBase/_toc.md")
            return;
        var segments = path.Split('/');
        if (
            segments.Length != 3
            || segments[0] != "KnowledgeBase"
            || !(
                string.Equals(segments[1], "system", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segments[1], ReviewBranchManager.RepoSlug(repo), StringComparison.OrdinalIgnoreCase)
            )
            || KnowledgeIndexRegenerator.IsDevelopersDirectory(segments[1])
            || KnowledgeIndexRegenerator.IsBookkeeping(segments[2])
            || !segments[2].EndsWith(".md", StringComparison.Ordinal)
            || segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."
                || !segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            )
        )
            throw new ArgumentException(
                "Knowledge edits must target a Markdown entry in this repository's or the system knowledge scope.",
                nameof(path)
            );
    }

    /// <summary>Applies the shared host containment check to every index read, including existing directory entries.</summary>
    private sealed class ContainedFileSystem(string root, ISandboxFileSystem inner) : ISandboxFileSystem
    {
        private void Validate(string path)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)).Replace('\\', '/');
            _ = WorkflowScriptInvoker.ResolveWorkspaceAsset(relative, root);
        }

        public Task<SandboxFileRead> ReadFileAsync(string path, long maxBytes, CancellationToken cancellationToken)
        {
            Validate(path);
            return inner.ReadFileAsync(path, maxBytes, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken)
        {
            Validate(directory);
            return inner.ListFilesAsync(directory, cancellationToken);
        }

        public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Knowledge bundle preparation is read-only.");
    }
}
