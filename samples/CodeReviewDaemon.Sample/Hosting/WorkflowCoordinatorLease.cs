namespace CodeReviewDaemon.Sample.Hosting;

/// <summary>Enforces the supported single-coordinator deployment for a SQLite workflow store.</summary>
internal static class WorkflowCoordinatorLease
{
    public static FileStream Acquire(string databasePath)
    {
        var path = Path.GetFullPath(databasePath) + ".coordinator.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another workflow coordinator owns this database. Stop it before starting a replacement.",
                exception
            );
        }
    }
}
