using System.Text.Json;
using System.Text.Json.Nodes;
using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The transcript-side board-loss metric (#621 Part B), pinned against the same discrimination line
/// the server detector uses: a not-found naming an id the thread minted and never deleted is a lost
/// row; anything else is a model typo.
/// </summary>
/// <remarks>
/// Every negative assertion here is paired with a positive one on the SAME ledger or the SAME thread.
/// An absence assertion passes just as readily when the code under test never ran, and the paired
/// positive is the only thing that rules that out.
/// </remarks>
public class BoardIdVanishTests
{
    private static string Vanished(string resultText) =>
        BoardIdLedger.NotFoundTaskId(resultText) ?? "(not a not-found)";

    [Theory]
    [InlineData("Error: Task '3' not found.", "3")]
    [InlineData("Error: Task 3 not found.", "3")]
    [InlineData("Error: Task '1.2' not found.", "1.2")]
    [InlineData("Error: Parent task '4' not found.", "4")]
    [InlineData("Error: Parent task 4 not found.", "4")]
    [InlineData("Error: Blocking task '5' not found.", "5")]
    [InlineData("Error: Task '1' has no subtask 3. subtaskId addresses one level BELOW taskId", "1.3")]
    [InlineData("Error: Task ' 07' not found.", "7")]
    public void NotFoundTaskId_ReadsTheIdOutOfEveryNotFoundWording(string text, string expected)
    {
        Vanished(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("Added task 1: Alpha")]
    [InlineData("Error: Invalid task ID format 'abc'.")]
    [InlineData("Error: Note index 4 out of range.")]
    public void NotFoundTaskId_IgnoresTextThatIsNotANotFound(string text)
    {
        BoardIdLedger.NotFoundTaskId(text).Should().BeNull();
    }

    [Fact]
    public void Ledger_OwnsAMintedId_AndNotAnIdItNeverSaw()
    {
        var ledger = new BoardIdLedger();
        ledger.RecordSuccess("add-task", "Added task 1: Alpha");

        ledger.Owns("9").Should().BeFalse("id 9 was never minted — that is a typo, not a loss");
        ledger
            .Owns("1")
            .Should()
            .BeTrue(
                "the identical probe on a minted id must succeed, which is what proves the miss above was a decision"
            );
    }

    [Fact]
    public void Ledger_ForgetsADeletedRow_AndEveryDescendantWithIt()
    {
        var ledger = new BoardIdLedger();
        ledger.RecordSuccess("add-task", "Added task 1: Alpha");
        ledger.RecordSuccess("add-task", "Added task 1.1: Child");
        ledger.RecordSuccess("add-task", "Added task 1.1.1: Grandchild");
        ledger.RecordSuccess("add-task", "Added task 2: Beta");

        ledger.RecordSuccess("delete-task", "Deleted task 1 and all subtasks: Alpha");

        ledger.Owns("1").Should().BeFalse();
        ledger.Owns("1.1").Should().BeFalse("the subtree goes with the row");
        ledger.Owns("1.1.1").Should().BeFalse("including the grandchild");
        ledger.Owns("2").Should().BeTrue("an unrelated row is untouched — the forget is scoped, not a clear");
    }

    [Fact]
    public void Ledger_ForgetsASubtaskDeletedByOrdinal()
    {
        var ledger = new BoardIdLedger();
        ledger.RecordSuccess("add-task", "Added task 1.2: Child");
        ledger.RecordSuccess("add-task", "Added task 1.3: Sibling");

        ledger.RecordSuccess("delete-task", "Deleted subtask 2 from task 1: Child");

        ledger.Owns("1.2").Should().BeFalse();
        ledger.Owns("1.3").Should().BeTrue("only the named ordinal is removed");
    }

    [Fact]
    public void Ledger_TakesTheIdsBulkInitializeNames_AndClearsOnAResetRun()
    {
        var ledger = new BoardIdLedger();
        ledger.RecordSuccess("bulk-initialize", "Added 2 task(s):\n  - Task 1: Alpha\n  - Task 2: Beta\n");

        ledger.Owns("1").Should().BeTrue();
        ledger.Owns("2").Should().BeTrue();

        ledger.RecordSuccess("bulk-initialize", "Cleared existing tasks.\nAdded 1 task(s):\n  - Task 1: Fresh\n");

        ledger.Owns("2").Should().BeFalse("clearExisting renumbers from 1, so the old ids are gone");
        ledger.Owns("1").Should().BeTrue("the reset run's own row is still minted");
    }

    /// <summary>
    ///     bulk-initialize now echoes its subtask rows too (#634 R1), indented one level deeper but in
    ///     the same shape, so the ledger picks them up with no regex change and one of its three
    ///     documented undercount gaps closes for transcripts recorded from here on.
    /// </summary>
    [Fact]
    public void Ledger_TakesTheSubtaskIdsBulkInitializeNowEchoes()
    {
        var ledger = new BoardIdLedger();
        ledger.RecordSuccess(
            "bulk-initialize",
            "Added 2 task(s) and 3 subtask(s):\n"
                + "  - Task 1: Alpha\n"
                + "    - Task 1.1: Alpha one\n"
                + "    - Task 1.2: Alpha two\n"
                + "  - Task 2: Beta\n"
                + "    - Task 2.1: Beta one\n"
        );

        ledger.Owns("1.1").Should().BeTrue();
        ledger.Owns("1.2").Should().BeTrue();
        ledger.Owns("2.1").Should().BeTrue();
        ledger.Owns("1").Should().BeTrue("the main tasks are still named");
        ledger.Owns("2.2").Should().BeFalse("nothing invents rows the echo did not name");
    }

    /// <summary>
    ///     The archived corpus. This is the VERBATIM payload every seed of the #617/#634 sweep got
    ///     back from <c>bulk-initialize</c> — byte-identical across runs that nested different numbers
    ///     of children, which is the defect #634 R1 names. Replayed through today's parser it behaves
    ///     exactly as it did when it was recorded: the three main ids land, and the subtask ids the
    ///     payload never named are still absent. That is the claim the ledger's XML docs and the
    ///     metrics spec make about pre-change transcripts, executed rather than asserted from the
    ///     regex — the gap did not widen, it simply stays open for text recorded before the fix.
    /// </summary>
    [Fact]
    public void Ledger_ParsesAPreChangeBulkInitializePayloadExactlyAsItDidBefore()
    {
        var ledger = new BoardIdLedger();
        ledger.RecordSuccess(
            "bulk-initialize",
            "Cleared existing tasks.\n"
                + "Added 3 task(s):\n"
                + "  - Task 1: Workstream 1 \u2014 Build & Test\n"
                + "  - Task 2: Workstream 2 \u2014 Documentation\n"
                + "  - Task 3: Workstream 3 \u2014 Packaging\n"
        );

        ledger.Owns("1").Should().BeTrue();
        ledger.Owns("2").Should().BeTrue();
        ledger.Owns("3").Should().BeTrue();

        // The run that produced this text had nested children under Workstream 1. The payload never
        // named them, so the ledger cannot know them — undercounting, exactly as before.
        ledger.Owns("1.1").Should().BeFalse("the old payload named no subtask ids, so none enter the ledger");
        ledger.Owns("4").Should().BeFalse();
    }

    /// <summary>
    ///     End to end through the real store reader: one synthetic thread whose transcript contains
    ///     both a genuine loss and the two things that must stay quiet.
    /// </summary>
    [Fact]
    public void ReadThread_ReportsOnlyTheLossesTheThreadCanAccountFor()
    {
        var thread = ReadSyntheticThread(includeDelete: true);

        var ids = thread.BoardIdVanishes.Select(v => v.TaskId).ToList();

        ids.Should().BeEquivalentTo(["1", "1.3"]);
        ids.Should().NotContain("9", "never minted in this thread");
        ids.Should().NotContain("2", "deliberately deleted by this thread");
        thread.BoardIdVanishes.Should().OnlyContain(v => v.ThreadId == "thread-vanish");
    }

    /// <summary>
    ///     The pin for the negative above: with the successful delete removed and nothing else
    ///     changed, the very same probe becomes a reported loss.
    /// </summary>
    [Fact]
    public void ReadThread_WithoutTheDelete_TheSameProbeBecomesALoss()
    {
        var thread = ReadSyntheticThread(includeDelete: false);

        thread.BoardIdVanishes.Select(v => v.TaskId).Should().Contain("2");
    }

    private static ConversationStoreReader.ThreadData ReadSyntheticThread(bool includeDelete)
    {
        var dir = Path.Combine(Path.GetTempPath(), "todoeval-vanish-" + Guid.NewGuid().ToString("N"), "thread-vanish");
        _ = Directory.CreateDirectory(dir);
        try
        {
            var envelopes = new JsonArray();
            var seq = 0;

            void Call(string tool, string args, string result)
            {
                var id = $"call_v_{++seq:D4}";
                envelopes.Add(
                    new JsonObject
                    {
                        ["messageType"] = "ToolCallMessage",
                        ["generationId"] = "gen-v",
                        ["role"] = "Assistant",
                        ["messageJson"] = new JsonObject
                        {
                            ["function_name"] = tool,
                            ["function_args"] = args,
                            ["tool_call_id"] = id,
                        }.ToJsonString(),
                    }
                );
                envelopes.Add(
                    new JsonObject
                    {
                        ["messageType"] = "ToolCallResultMessage",
                        ["generationId"] = "gen-v",
                        ["role"] = "Tool",
                        ["messageJson"] = new JsonObject
                        {
                            ["tool_call_id"] = id,
                            ["result"] = result,
                            ["is_error"] = false,
                        }.ToJsonString(),
                    }
                );
            }

            Call("add-task", "{\"title\":\"Alpha\"}", "Added task 1: Alpha");
            Call("add-task", "{\"title\":\"Gamma\",\"parentId\":\"1\"}", "Added task 1.3: Gamma");
            Call("add-task", "{\"title\":\"Beta\"}", "Added task 2: Beta");
            if (includeDelete)
            {
                Call("delete-task", "{\"taskId\":\"2\"}", "Deleted task 2 and all subtasks: Beta");
            }

            Call("get-task", "{\"taskId\":\"9\"}", "Error: Task '9' not found.");
            Call("get-task", "{\"taskId\":\"2\"}", "Error: Task '2' not found.");
            Call("get-task", "{\"taskId\":\" 01\"}", "Error: Task ' 01' not found.");
            Call("add-note", "{\"taskId\":\"1\",\"subtaskId\":3}", "Error: Task '1' has no subtask 3.");

            File.WriteAllText(
                Path.Combine(dir, "messages.json"),
                envelopes.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
            );

            return ConversationStoreReader.LoadThread(dir);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }
}
