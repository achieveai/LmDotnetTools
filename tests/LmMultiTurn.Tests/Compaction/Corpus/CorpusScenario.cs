using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

namespace LmMultiTurn.Tests.Compaction.Corpus;

/// <summary>One thing the runner does to the loop, in order.</summary>
public sealed record CorpusStep
{
    /// <summary>say | board | resolve | await_runs | release | restart.</summary>
    public required string Kind { get; init; }

    /// <summary>For <c>say</c>: the user text. For <c>resolve</c>: the tool result.</summary>
    public string? Text { get; init; }

    /// <summary>For <c>say</c>: a correction injected while the run is in flight, once the provider has served <see cref="AfterCall"/> calls (R4).</summary>
    public string? Inject { get; init; }

    public int AfterCall { get; init; }

    /// <summary>For <c>say</c>: whether the run is expected to end in error (b).</summary>
    public bool ExpectError { get; init; }

    /// <summary>For <c>resolve</c>: the deferred tool call id. For <c>release</c>: the gate.</summary>
    public string? Id { get; init; }

    /// <summary>For <c>await_runs</c>/<c>release</c>: how many further run completions to wait for.</summary>
    public int Runs { get; init; }

    public static CorpusStep Say(string text, bool expectError = false) =>
        new()
        {
            Kind = "say",
            Text = text,
            ExpectError = expectError,
        };

    public static CorpusStep SayWithCorrection(string text, int afterCall, string correction) =>
        new()
        {
            Kind = "say",
            Text = text,
            AfterCall = afterCall,
            Inject = correction,
        };

    public static CorpusStep Board() => new() { Kind = "board" };

    public static CorpusStep Resolve(string toolCallId, string result) =>
        new()
        {
            Kind = "resolve",
            Id = toolCallId,
            Text = result,
        };

    public static CorpusStep AwaitRuns(int runs) => new() { Kind = "await_runs", Runs = runs };

    public static CorpusStep Release(string gate, int runs) =>
        new()
        {
            Kind = "release",
            Id = gate,
            Runs = runs,
        };

    public static CorpusStep Restart() => new() { Kind = "restart" };
}

/// <summary>Which public price list the scenario runs under (D4).</summary>
public enum CorpusPricing
{
    /// <summary>Prompt, completion and cache rates: every category priced.</summary>
    Full,

    /// <summary>Prompt and completion only: cache reads are an unpriced category (m).</summary>
    NoCacheRates,

    /// <summary>No public pricing for the model (l).</summary>
    None,
}

/// <summary>
/// One corpus item (spec 679 §12.4 (a)-(m)): the whole input to a run - user steps, the root's reply
/// table, the children's reply tables, the window, the model, the price list and the store flavour - so
/// that its SHA-256 fingerprint pins exactly what every mode was evaluated against.
/// </summary>
public sealed record CorpusScenario
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>The §12.4 item this scenario is.</summary>
    public required string Item { get; init; }

    public required IReadOnlyList<CorpusStep> Steps { get; init; }

    public required CorpusScript Root { get; init; }

    /// <summary>Sub-agent template name → reply table for that child.</summary>
    public IReadOnlyDictionary<string, CorpusScript> Children { get; init; } =
        new Dictionary<string, CorpusScript>(StringComparer.Ordinal);

    public long? WindowTokens { get; init; } = CorpusScenarios.Window;

    public string ModelId { get; init; } = CorpusScenarios.Model;

    public CorpusPricing Pricing { get; init; } = CorpusPricing.Full;

    /// <summary>memory | file-legacy (rows seeded without Seq, k).</summary>
    public string Store { get; init; } = "memory";

    /// <summary>Legacy rows to seed before the first run (k); text per row.</summary>
    public IReadOnlyList<string> LegacyRows { get; init; } = [];

    /// <summary>Todo board task titles saved by the <c>board</c> step (Tasks class, V4).</summary>
    public IReadOnlyList<string> BoardTasks { get; init; } = [];

    public bool IncludeAskUserQuestionTool { get; init; }

    public bool IncludeWaitTool { get; init; }

    /// <summary>The loop runs as a workflow controller (e).</summary>
    public bool WorkflowController { get; init; }

    /// <summary>
    /// The scripted conversation grows past the window, so a mode that never rewrites the request
    /// (Off, Shadow) is expected to end in a provider overflow while Compact is expected to finish.
    /// </summary>
    public bool ExceedsWindow { get; init; }

    /// <summary>A skip reason Compact/Shadow must record at least once (l: <c>capacity_unknown</c>).</summary>
    public string? MustSkipWith { get; init; }

    /// <summary>
    /// The provider call whose reply parks the run (a deferred question, a Wait). Between that call and the
    /// resumption no request may be built and no cut may land (R6); the runner records the call count at
    /// each step so the evaluator can prove it.
    /// </summary>
    public int? ParksAtCall { get; init; }

    /// <summary>Whether Compact mode is expected to activate at least one checkpoint on the root thread.</summary>
    public bool ExpectRootCompaction { get; init; } = true;

    /// <summary>Whether Compact mode is expected to activate at least one checkpoint on a child thread (d).</summary>
    public bool ExpectChildCompaction { get; init; }

    public bool ExpectedSuccess(CompactionMode mode) => !ExceedsWindow || mode == CompactionMode.Compact;

    /// <summary>SHA-256 of the scenario's declarative definition, hex (D2).</summary>
    public string Fingerprint()
    {
        var json = JsonSerializer.Serialize(this, FingerprintJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions FingerprintJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
