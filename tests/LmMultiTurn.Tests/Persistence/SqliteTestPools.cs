using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace LmMultiTurn.Tests.Persistence;

internal static class SqliteTestPools
{
    public static void Clear(string databasePath)
    {
        SqliteConnectionFactory.ClearPool(databasePath);

        // Raw SQL probes in these fixtures use either the default string or an explicit open mode.
        // Pools are keyed by the exact string, so clear both without touching other databases.
        using var defaults = new SqliteConnection($"Data Source={databasePath}");
        SqliteConnection.ClearPool(defaults);
        using var migration = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString()
        );
        SqliteConnection.ClearPool(migration);
    }

    public static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var databasePath in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
            Clear(databasePath);
    }
}
