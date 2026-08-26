using Microsoft.Data.Sqlite;

namespace CodeReviewDaemon.Sample.Persistence.Migrations;

internal sealed record SchemaCompatibilityReport(
    long CurrentVersion,
    long MinimumSupportedVersion,
    long MaximumSupportedVersion,
    bool IsCompatible,
    bool RequiresMigration,
    string? FailureReason
);

internal static class SchemaCompatibility
{
    public static SchemaCompatibilityReport Inspect(
        SqliteConnection connection,
        long minimumSupportedVersion = 0,
        long? maximumSupportedVersion = null
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        var maximum = maximumSupportedVersion ?? MigrationRunner.LatestVersion;
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt64(command.ExecuteScalar());
        var compatible = current >= minimumSupportedVersion && current <= maximum;
        var reason =
            current < minimumSupportedVersion
                ? $"Database schema {current} is older than supported minimum {minimumSupportedVersion}."
            : current > maximum ? $"Database schema {current} is newer than supported maximum {maximum}."
            : null;
        return new SchemaCompatibilityReport(
            current,
            minimumSupportedVersion,
            maximum,
            compatible,
            current < maximum,
            reason
        );
    }
}

internal interface IDatabaseActivationGate
{
    SchemaCompatibilityReport Inspect();
    string CreateBackup(string destinationPath);
    void ValidateMigrationOnCopy(string backupPath);
    void MigrateHeldDatabase();
}

internal sealed class SqliteDatabaseActivationGate(string databasePath) : IDatabaseActivationGate
{
    public SchemaCompatibilityReport Inspect()
    {
        using var connection = Open(databasePath);
        return SchemaCompatibility.Inspect(connection);
    }

    public string CreateBackup(string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        using var source = Open(databasePath);
        using var destination = Open(destinationPath);
        source.BackupDatabase(destination);
        return destinationPath;
    }

    public void ValidateMigrationOnCopy(string backupPath)
    {
        var copy = backupPath + ".migration-test";
        File.Copy(backupPath, copy, overwrite: true);
        try
        {
            using var connection = Open(copy);
            MigrationRunner.Migrate(connection);
            if (!SchemaCompatibility.Inspect(connection).IsCompatible)
            {
                throw new InvalidOperationException("Migrated database copy is not schema-compatible.");
            }
        }
        finally
        {
            File.Delete(copy);
        }
    }

    public void MigrateHeldDatabase()
    {
        using var connection = Open(databasePath);
        MigrationRunner.Migrate(connection);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        return connection;
    }
}
