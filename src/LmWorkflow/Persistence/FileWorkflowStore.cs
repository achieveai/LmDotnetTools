using System.Security.Cryptography;
using System.Text;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Persistence;

/// <summary>
/// Durable snapshots for a single admitted owner per instance. A flushed temporary file replaces the
/// prior snapshot atomically; incomplete temporary files are never loaded as progress.
/// </summary>
public sealed class FileWorkflowStore : IWorkflowStore
{
    private readonly string _directory;

    public FileWorkflowStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = Path.GetFullPath(directory);
        _ = Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(string instanceId, WorkflowInstanceSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(instanceId, snapshot.InstanceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Snapshot identity does not match its store key.", nameof(snapshot));
        }

        var target = SnapshotPath(instanceId);
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(snapshot.ToJson());
            await using (
                var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough
                )
            )
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<WorkflowInstanceSnapshot?> LoadAsync(string instanceId, CancellationToken ct = default)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(SnapshotPath(instanceId), ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var snapshot = WorkflowInstanceSnapshot.FromJson(json);
        if (!string.Equals(snapshot.InstanceId, instanceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored snapshot identity does not match its key.");
        }
        return snapshot;
    }

    public Task DeleteAsync(string instanceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        File.Delete(SnapshotPath(instanceId));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        var ids = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var snapshot = WorkflowInstanceSnapshot.FromJson(
                await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)
            );
            if (
                !string.Equals(
                    Path.GetFileName(path),
                    Path.GetFileName(SnapshotPath(snapshot.InstanceId)),
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException("Stored snapshot identity does not match its filename.");
            }
            ids.Add(snapshot.InstanceId);
        }
        return ids;
    }

    private string SnapshotPath(string instanceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        return Path.Combine(
            _directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId))) + ".json"
        );
    }
}
