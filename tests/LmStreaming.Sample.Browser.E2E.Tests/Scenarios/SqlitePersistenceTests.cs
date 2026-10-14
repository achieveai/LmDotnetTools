using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Proves the host persists conversations to the SQLite store (not the former file store) and that the
/// data is queryable by thread id — the core of the file→SQLite migration. Drives a two-turn
/// conversation through the real chat client, then opens the host's SQLite database directly and asserts
/// the merged messages and thread metadata landed in the <c>messages</c> / <c>thread_metadata</c> tables.
/// The former <c>FileConversationStore</c> would have produced a <c>conversations/</c> directory tree
/// instead, so this test fails closed if the wiring ever regresses back to files.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class SqlitePersistenceTests
{
    private readonly PlaywrightFixture _fixture;

    public SqlitePersistenceTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Conversation_is_persisted_to_sqlite_and_queryable_by_thread_id()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Thinking(16).Text("First answer: forty-two."))
            .Turn(t => t.Thinking(16).Text("Second answer: still forty-two."))
            .Build();

        await using var session = await _fixture.OpenAsync("test-anthropic", responder.HandlerFor("test-anthropic"));
        var page = session.Page;
        await page.SelectDeveloperViewAsync();

        await page.SendMessageAsync("what is the meaning of life?");
        await page.WaitForStreamIdleAsync();
        await page.SendMessageAsync("are you sure?");
        await page.WaitForStreamIdleAsync();
        await page.AssistantText().WaitForCountAtLeastAsync(2);

        // The conversation now exists. Read the SQLite database the host wrote to, directly, to prove it
        // is SQLite-backed and queryable by id. Open read-only so we never contend with the host's
        // writers; shared cache lets a reader see the WAL-committed rows the host just appended.
        var dbPath = session.Factory.ConversationDbPath;
        File.Exists(dbPath).Should().BeTrue($"the host should have created the SQLite database at {dbPath}");

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
            }.ToString()
        );
        await connection.OpenAsync();

        // Exactly one conversation thread was created by this isolated factory.
        var threadId = await ScalarAsync(connection, "SELECT thread_id FROM thread_metadata LIMIT 1;");
        threadId.Should().NotBeNullOrEmpty("the conversation's thread metadata should be persisted");

        // The two user prompts and two assistant answers are all persisted as merged messages, keyed by
        // that thread id (queryable by conversation id — the whole point of the migration).
        var totalForThread = Convert.ToInt32(
            await ScalarAsync(connection, "SELECT COUNT(*) FROM messages WHERE thread_id = $t;", ("$t", threadId!))
        );
        totalForThread.Should().BeGreaterThanOrEqualTo(4, "two user turns and two assistant replies are persisted");

        var userCount = Convert.ToInt32(
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM messages WHERE thread_id = $t AND role = 'User';",
                ("$t", threadId!)
            )
        );
        userCount.Should().BeGreaterThanOrEqualTo(2, "both user prompts are persisted");

        var assistantCount = Convert.ToInt32(
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM messages WHERE thread_id = $t AND role = 'Assistant';",
                ("$t", threadId!)
            )
        );
        assistantCount.Should().BeGreaterThanOrEqualTo(2, "both assistant replies are persisted");

        // The persisted assistant text is the real merged content, not a streaming fragment.
        var answerJson = await ScalarAsync(
            connection,
            "SELECT group_concat(message_json, ' ') FROM messages WHERE thread_id = $t AND role = 'Assistant';",
            ("$t", threadId!)
        );
        answerJson.Should().Contain("forty-two", "the merged assistant answer text is stored in SQLite");
    }

    private static async Task<string?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, string Value)[] parameters
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
