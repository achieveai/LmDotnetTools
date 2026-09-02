using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Builds the row shapes the cut, projection and validation fixtures share (#683; spec 679 §12.2):
/// runs, human rows, generations of assistant text or one tool call/result pair, deferred placeholders,
/// notifications and checkpoint rows. Every row gets a dense <c>Seq</c>, an id <c>m{seq}</c> and the run
/// it was added under, the way <see cref="SequencedHistory.FromPersisted"/> would stamp it.
/// </summary>
internal sealed class ThreadFixture
{
    /// <summary>A size estimator that makes arithmetic in the fixtures exact: ten tokens per row.</summary>
    public const long TokensPerRow = 10;

    private readonly List<SequencedMessage> _rows = [];
    private string _runId = "run-1";
    private int _generation;

    public IReadOnlyList<SequencedMessage> Rows => _rows;

    public IReadOnlyList<IMessage> Messages => [.. _rows.Select(r => r.Message)];

    public long LastSeq => _rows.Count == 0 ? 0 : _rows[^1].Seq;

    public string RunId => _runId;

    public static long RowTokens(IMessage _) => TokensPerRow;

    public static CutSelectorOptions Options(long minTail = 100, long maxTail = 10_000, int lookback = 3) =>
        new()
        {
            MinTailTokens = minTail,
            MaxTailTokens = maxTail,
            CorrectionLookbackRuns = lookback,
            Estimator = RowTokens,
        };

    public CutRequest Request(
        long candidateSeq,
        CutSelectorOptions? options = null,
        CutBlockingState? loopState = null,
        IReadOnlyList<RunLedgerEntry>? runs = null,
        long? activeBoundarySeq = null
    ) =>
        new(
            Rows,
            candidateSeq,
            loopState ?? CutBlockingState.Clean,
            runs ?? [],
            activeBoundarySeq,
            options ?? Options()
        );

    /// <summary>Starts a new run; every later row carries <paramref name="runId"/>.</summary>
    public ThreadFixture Run(string runId)
    {
        _runId = runId;
        return this;
    }

    public ThreadFixture Human(string text) =>
        Add(
            new TextMessage
            {
                Text = text,
                Role = Role.User,
                RunId = _runId,
            }
        );

    public ThreadFixture Assistant(string text) =>
        Add(
            new TextMessage
            {
                Text = text,
                Role = Role.Assistant,
                RunId = _runId,
                GenerationId = NextGeneration(),
            }
        );

    /// <summary>
    /// One completed tool turn: a call and its result, both in one generation. The pair is what R1 keeps
    /// on one side of a cut; <paramref name="deferred"/> makes the result the placeholder R6 protects.
    /// </summary>
    public ThreadFixture ToolTurn(
        string tool = "Bash",
        string? args = null,
        string result = "ok",
        bool deferred = false,
        string? generationId = null
    )
    {
        var generation = generationId ?? NextGeneration();
        var id = $"call-{_rows.Count + 1}";
        _ = Add(
            new ToolCallMessage
            {
                ToolCallId = id,
                FunctionName = tool,
                FunctionArgs = args ?? $$"""{"row":{{_rows.Count + 1}}}""",
                RunId = _runId,
                GenerationId = generation,
            }
        );
        return Add(
            new ToolCallResultMessage
            {
                ToolCallId = id,
                ToolName = tool,
                Result = result,
                IsDeferred = deferred,
                RunId = _runId,
                GenerationId = generation,
            }
        );
    }

    /// <summary>
    /// <paramref name="count"/> tool turns; the one at <paramref name="deferredAt"/> (1-based) is deferred
    /// and calls <paramref name="deferredTool"/>.
    /// </summary>
    public ThreadFixture ToolTurns(int count, int? deferredAt = null, string deferredTool = "AskUserQuestion")
    {
        for (var turn = 1; turn <= count; turn++)
        {
            var deferred = turn == deferredAt;
            _ = ToolTurn(
                tool: deferred ? deferredTool : "Bash",
                args: $$"""{"turn":{{turn}}}""",
                result: deferred ? "pending" : $"output of turn {turn}",
                deferred: deferred
            );
        }

        return this;
    }

    /// <summary>
    /// One call row with no result: the stream ended between the call and the tool running. The next row
    /// belongs to a later generation, so only R1's pairing half can refuse a cut here.
    /// </summary>
    public ThreadFixture ToolCall(string tool = "Bash") =>
        Add(
            new ToolCallMessage
            {
                ToolCallId = $"call-{_rows.Count + 1}",
                FunctionName = tool,
                FunctionArgs = $$"""{"row":{{_rows.Count + 1}}}""",
                RunId = _runId,
                GenerationId = NextGeneration(),
            }
        );

    public ThreadFixture Notify(string kind = "subagent-completion", string label = "agent-1 finished") =>
        Add(
            new NotifyMessage
            {
                NotifyKind = kind,
                Label = label,
                RunId = _runId,
            }
        );

    public ThreadFixture Checkpoint(CompactionCheckpointMessage checkpoint) => Add(checkpoint);

    /// <summary>The seq of the result row that closes tool turn <paramref name="turn"/> when the run opened with one human row.</summary>
    public static long TurnEnd(int turn) => (2L * turn) + 1;

    private string NextGeneration() => $"gen-{++_generation}";

    private ThreadFixture Add(IMessage message)
    {
        var seq = _rows.Count + 1;
        _rows.Add(new SequencedMessage(seq, $"m{seq}", _runId, message));
        return this;
    }
}
