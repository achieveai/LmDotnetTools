namespace CodeReviewDaemon.Sample.Persistence.Migrations;

/// <summary>
/// One forward schema migration. <see cref="Version"/> is the <c>PRAGMA user_version</c> the database
/// reaches once <see cref="Sql"/> has been applied. Migrations are pure DDL strings so they run inside
/// the runner's single transaction and roll back atomically on crash.
/// </summary>
internal sealed record Migration(long Version, string Sql);
