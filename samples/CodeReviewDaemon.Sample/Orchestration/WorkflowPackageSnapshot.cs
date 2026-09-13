using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Copies admitted instructions and scripts once; resume verifies and uses those exact bytes.</summary>
internal static class WorkflowPackageSnapshot
{
    public static async Task<string> PrepareAsync(
        string workflowPath,
        string runDirectory,
        bool resuming,
        CancellationToken ct
    )
    {
        var destination = Path.Combine(runDirectory, "package");
        var manifestPath = Path.Combine(destination, "manifest.json");
        if (!Directory.Exists(destination))
        {
            if (resuming)
                throw new InvalidDataException(
                    "This workflow predates immutable package admission and requires manual reconciliation."
                );
            var root = Path.GetDirectoryName(workflowPath)!;
            var workflowBytes = await File.ReadAllBytesAsync(workflowPath, ct).ConfigureAwait(false);
            var yaml = Encoding.UTF8.GetString(workflowBytes).TrimStart('\uFEFF');
            var definition = SimpleWorkflow.DeserializeYaml(yaml).ToDefinition();
            var paths = new HashSet<string>(StringComparer.Ordinal) { Path.GetFileName(workflowPath) };
            foreach (var task in definition.Nodes.OfType<ProceduralNode>().SelectMany(node => node.TaskList ?? []))
            {
                foreach (var skill in task.Skills ?? [])
                    paths.Add(skill);
                if (task.Script is { } script)
                {
                    paths.Add(script);
                    var source = WorkflowScriptInvoker.ResolveWorkspaceAsset(script, root);
                    // Shipped wrappers import neighbouring transport helpers.
                    foreach (var helper in Directory.EnumerateFiles(Path.GetDirectoryName(source)!))
                        if (Path.GetExtension(helper) is ".py" or ".ps1")
                            paths.Add(Path.GetRelativePath(root, helper).Replace('\\', '/'));
                }
            }
            var temporary = Path.Combine(runDirectory, "package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var relative in paths)
            {
                var source = WorkflowScriptInvoker.ResolveWorkspaceAsset(relative, root);
                var bytes =
                    relative == Path.GetFileName(workflowPath)
                        ? workflowBytes
                        : await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
                var target = WorkflowScriptInvoker.ResolveWorkspaceAsset(relative, temporary);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
                hashes.Add(relative, Convert.ToHexString(SHA256.HashData(bytes)));
            }
            await File.WriteAllTextAsync(Path.Combine(temporary, "manifest.json"), JsonSerializer.Serialize(hashes), ct)
                .ConfigureAwait(false);
            Directory.Move(temporary, destination);
        }
        var admitted =
            JsonSerializer.Deserialize<Dictionary<string, string>>(
                await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false)
            ) ?? throw new InvalidDataException("Admitted package manifest is invalid.");
        if (!admitted.ContainsKey(Path.GetFileName(workflowPath)))
            throw new InvalidDataException("Admitted package is missing its workflow definition.");
        foreach (var (relative, hash) in admitted)
        {
            var bytes = await File.ReadAllBytesAsync(
                    WorkflowScriptInvoker.ResolveWorkspaceAsset(relative, destination),
                    ct
                )
                .ConfigureAwait(false);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != hash)
                throw new InvalidDataException("Admitted workflow package was modified.");
        }
        return Path.Combine(destination, Path.GetFileName(workflowPath));
    }
}
