using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

/// <summary>Limits for one <see cref="MapReadToolProvider.ToolName"/> call.</summary>
public sealed record MapReadOptions
{
    /// <summary>Documents read by the cheap model at the same time.</summary>
    public int MaxParallel { get; init; } = 8;

    /// <summary>Most documents one call may name. More is refused before anything is read.</summary>
    public int MaxDocuments { get; init; } = 400;

    /// <summary>A document longer than this is split at line ends and each part read on its own.</summary>
    public int ChunkChars { get; init; } = 60_000;

    /// <summary>
    /// Output budget for one reader call. The record is one line, but a reasoning model spends its
    /// thinking from the same budget: 600 left most gpt-6-luna calls with no text at all (m1, 348 of ~370).
    /// </summary>
    public int MaxOutputTokens { get; init; } = 4096;
}

/// <summary>
/// The planner's one tool for reading many documents at once (the MinionS pattern: the strong model
/// decomposes, the cheap model reads chunks in parallel and abstains on the irrelevant ones, the
/// strong model sees only what remains). One planner call names an instruction and the documents;
/// the host reads each with the executor's own Read tool, hands each to the cheap model as a single
/// prompt, and returns one line per document. Documents the reader abstains on are left out, so the
/// planner's context grows only by what bears on its question. Every reader call is billed to the
/// executor's usage row.
/// </summary>
public sealed class MapReadToolProvider : IFunctionProvider
{
    /// <summary>The tool's name on the planner's wire.</summary>
    public const string ToolName = "map_read";

    /// <summary>The reply that drops a document from the result.</summary>
    public const string AbstainMarker = "ABSTAIN";

    private const string ReaderSystemPrompt = """
        You read ONE document for a planner that cannot see it. You do not think out loud. Reply with
        exactly one line: the fields the instruction asks for, in that order, separated by ` | `.
        Facts only, as the document states them; never a verdict. Write "-" for a field the document
        is silent on. Quote under 12 words, only where a field needed judgment. No preamble,
        no headings, no markdown, no restating the question.
        If the document has nothing that bears on the instruction, reply with the single word ABSTAIN.
        """;

    private static readonly Regex LineNumberPrefix = new(@"^\s*\d+(?:→|\t| \|)", RegexOptions.Compiled);

    private readonly IAgent _reader;
    private readonly GenerateReplyOptions _readerOptions;
    private readonly ToolHandler _readFile;
    private readonly ToolHandler? _glob;
    private readonly IUsageSink _usage;
    private readonly string _executorThreadId;
    private readonly MapReadOptions _options;
    private readonly ILogger _logger;

    /// <param name="reader">The cheap model, called once per document part.</param>
    /// <param name="readerOptions">Options for those calls. The model id is what the usage row names.</param>
    /// <param name="readFile">The executor's Read tool: <c>{"file_path": ...}</c> in, the text out.</param>
    /// <param name="glob">The executor's Glob tool, or null when callers must name paths.</param>
    /// <param name="usage">Where the reader's usage goes: the executor's sink.</param>
    /// <param name="executorThreadId">The executor's thread id, the owner of every usage record.</param>
    /// <param name="options">Limits, or the defaults.</param>
    /// <param name="logger">Optional.</param>
    public MapReadToolProvider(
        IAgent reader,
        GenerateReplyOptions readerOptions,
        ToolHandler readFile,
        ToolHandler? glob,
        IUsageSink usage,
        string executorThreadId,
        MapReadOptions? options = null,
        ILogger<MapReadToolProvider>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(readerOptions);
        ArgumentNullException.ThrowIfNull(readFile);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentException.ThrowIfNullOrWhiteSpace(executorThreadId);
        _reader = reader;
        _readerOptions = readerOptions;
        _readFile = readFile;
        _glob = glob;
        _usage = usage;
        _executorThreadId = executorThreadId;
        _options = options ?? new MapReadOptions();
        _logger = logger ?? NullLogger<MapReadToolProvider>.Instance;
    }

    /// <inheritdoc />
    public string ProviderName => "DualLayerMapRead";

    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return new FunctionDescriptor
        {
            Contract = new FunctionContract
            {
                Name = ToolName,
                Description =
                    "Read many documents at once and get one short record per document. Each document is read "
                    + "in full by a separate cheap reader, in parallel; you never see the text. Name the fields you "
                    + "want, in order, and when the reader should abstain. Documents the reader abstains on are "
                    + "left out of the result. Use this instead of reading documents one by one or in batches "
                    + "whenever a question needs a fact from each of several documents.",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "instruction",
                        Description =
                            "What to extract from EACH document: the fields in order (for example 'ticket id | "
                            + "customer tier | outage start | outage end | credit requested'), what 'not stated' "
                            + "means here, and when to abstain (for example 'abstain unless the ticket mentions an "
                            + "outage'). Facts, not verdicts: you apply the rules yourself.",
                        ParameterType = new JsonSchemaObject { Type = new("string") },
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "paths",
                        Description = "Absolute paths of the documents to read, one per line. Use this and/or glob.",
                        ParameterType = new JsonSchemaObject { Type = new("string") },
                        IsRequired = false,
                    },
                    new FunctionParameterContract
                    {
                        Name = "glob_path",
                        Description =
                            "Absolute directory to search when glob is given, for example /workspace/tickets.",
                        ParameterType = new JsonSchemaObject { Type = new("string") },
                        IsRequired = false,
                    },
                    new FunctionParameterContract
                    {
                        Name = "glob",
                        Description = "Pattern under glob_path, for example *.md or **/*.txt.",
                        ParameterType = new JsonSchemaObject { Type = new("string") },
                        IsRequired = false,
                    },
                    new FunctionParameterContract
                    {
                        Name = "allow_long",
                        Description =
                            "\"true\" to read a document longer than one part (about 60k chars) part by part. "
                            + "Off by default: a reader that sees one slice of a long report cannot tell the "
                            + "right statement or year, so such documents are skipped with a note. Use Grep "
                            + "and Read for one figure in a long report.",
                        ParameterType = new JsonSchemaObject { Type = new("string") },
                        IsRequired = false,
                    },
                    new FunctionParameterContract
                    {
                        Name = "rationale",
                        Description = "Optional here: the instruction is what the readers act on.",
                        ParameterType = new JsonSchemaObject { Type = new("string") },
                        IsRequired = false,
                    },
                ],
                ReturnType = typeof(string),
                ReturnDescription = "One line per document that was not abstained on: path | record.",
            },
            Handler = HandleAsync,
            ProviderName = ProviderName,
        };
    }

    private async Task<ToolHandlerResult> HandleAsync(string argsJson, ToolCallContext context, CancellationToken ct)
    {
        JsonObject? args;
        try
        {
            args = string.IsNullOrWhiteSpace(argsJson) ? [] : JsonNode.Parse(argsJson) as JsonObject;
        }
        catch (JsonException ex)
        {
            return ToolHandlerResult.FromError($"Rejected: the arguments are not valid JSON ({ex.Message}).");
        }

        var instruction = Text(args, "instruction");
        if (instruction.Length == 0)
        {
            return ToolHandlerResult.FromError(
                "Rejected: 'instruction' is required: the fields to extract from each document."
            );
        }

        List<string> paths;
        try
        {
            paths = await ResolvePathsAsync(
                    Text(args, "paths"),
                    Text(args, "glob_path"),
                    Text(args, "glob"),
                    context,
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ToolHandlerResult.FromError($"Rejected: {ex.Message}");
        }

        if (paths.Count == 0)
        {
            return ToolHandlerResult.FromError("Rejected: no documents. Give 'paths', or 'glob_path' and 'glob'.");
        }

        if (paths.Count > _options.MaxDocuments)
        {
            return ToolHandlerResult.FromError(
                $"Rejected: {paths.Count} documents is more than the {_options.MaxDocuments} one call may read. Split the set."
            );
        }

        _logger.LogInformation(
            "map_read (call {ToolCallId}): {DocumentCount} documents, instruction {InstructionLength} chars",
            context.ToolCallId,
            paths.Count,
            instruction.Length
        );

        using var gate = new SemaphoreSlim(_options.MaxParallel, _options.MaxParallel);
        var allowLong = Text(args, "allow_long").Equals("true", StringComparison.OrdinalIgnoreCase);
        var results = await Task.WhenAll(
                paths.Select(p => ReadDocumentAsync(p, instruction, allowLong, gate, context, ct))
            )
            .ConfigureAwait(false);

        var reported = 0;
        var abstained = 0;
        var errors = 0;
        var tooLong = 0;
        var parts = 0;
        // The planner pays for every character it gets back, so a shared directory is named once.
        var prefix = CommonDirectory(paths);
        var lines = new StringBuilder();
        var abstainedNames = new List<string>();
        foreach (var r in results)
        {
            parts += r.Parts;
            var name = r.Path[prefix.Length..];
            if (r.TooLong)
            {
                tooLong++;
                _ = lines.Append(name).Append(" | ").Append(r.Error).Append('\n');
            }
            else if (r.Error is not null)
            {
                errors++;
                _ = lines.Append(name).Append(" | ERROR: ").Append(r.Error).Append('\n');
            }
            else if (r.Records.Count == 0)
            {
                abstained++;
                abstainedNames.Add(name);
            }
            else
            {
                reported++;
                foreach (var record in r.Records)
                {
                    _ = lines.Append(name).Append(" | ").Append(record).Append('\n');
                }
            }
        }

        var header =
            $"map_read: {paths.Count} documents read ({parts} parts), {reported} reported, {abstained} abstained, {errors} errors."
            + (tooLong == 0 ? string.Empty : $" {tooLong} skipped as too long.");
        _logger.LogInformation("{Header} (call {ToolCallId})", header, context.ToolCallId);
        if (lines.Length == 0)
        {
            return ToolHandlerResult.FromText(header);
        }

        // Over a glob, abstaining is the filter. Over named paths the planner chose every document, so a
        // document that vanished silently is a lost fact (m3: a reader abstained on one of 112 tickets the
        // planner had already kept, and the answer lost that ticket). Name them.
        if (abstainedNames.Count > 0 && Text(args, "glob").Length == 0)
        {
            _ = lines.Append("abstained: ").Append(string.Join(", ", abstainedNames)).Append('\n');
        }

        var where = prefix.Length == 0 ? string.Empty : $" Paths are under {prefix} (- = not stated).";
        return ToolHandlerResult.FromText(header + where + "\n" + lines.ToString().TrimEnd());
    }

    /// <summary>The directory every path shares, with its trailing separator; empty when there is none.</summary>
    internal static string CommonDirectory(IReadOnlyList<string> paths)
    {
        if (paths.Count < 2)
        {
            return string.Empty;
        }

        var first = paths[0];
        var common = first.Length;
        foreach (var p in paths.Skip(1))
        {
            var n = Math.Min(common, p.Length);
            var i = 0;
            while (i < n && first[i] == p[i])
            {
                i++;
            }

            common = i;
        }

        var cut = common == 0 ? -1 : first.LastIndexOfAny(['/', '\\'], common - 1);
        return cut < 0 ? string.Empty : first[..(cut + 1)];
    }

    private static string Text(JsonObject? args, string name) =>
        args?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty;

    private async Task<List<string>> ResolvePathsAsync(
        string paths,
        string globPath,
        string glob,
        ToolCallContext context,
        CancellationToken ct
    )
    {
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var raw in paths.Split(
                ['\n', '\r', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (seen.Add(raw))
            {
                resolved.Add(raw);
            }
        }

        if (glob.Length == 0)
        {
            return resolved;
        }

        if (_glob is null)
        {
            throw new InvalidOperationException("this host has no Glob tool; name the paths instead.");
        }

        if (globPath.Length == 0)
        {
            throw new InvalidOperationException("'glob' needs 'glob_path', the absolute directory to search.");
        }

        var globArgs = new JsonObject
        {
            ["pattern"] = glob,
            ["path"] = globPath,
            ["max_results"] = 0,
        };
        var result = await _glob(globArgs.ToJsonString(), context, ct).ConfigureAwait(false);
        if (result is not ToolHandlerResult.Resolved resolvedGlob || resolvedGlob.Payload.IsError)
        {
            throw new InvalidOperationException($"Glob failed: {ResultText(result)}");
        }

        // The sandbox Glob prints one absolute path per line, then a blank line and "N files matched".
        foreach (
            var line in resolvedGlob.Payload.Text.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if ((line.StartsWith('/') || line.Contains(":\\", StringComparison.Ordinal)) && seen.Add(line))
            {
                resolved.Add(line);
            }
        }

        return resolved;
    }

    private sealed record DocumentResult(
        string Path,
        List<string> Records,
        int Parts,
        string? Error,
        bool TooLong = false
    );

    private async Task<DocumentResult> ReadDocumentAsync(
        string path,
        string instruction,
        bool allowLong,
        SemaphoreSlim gate,
        ToolCallContext context,
        CancellationToken ct
    )
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var readArgs = new JsonObject { ["file_path"] = path };
            var read = await _readFile(readArgs.ToJsonString(), context, ct).ConfigureAwait(false);
            if (read is not ToolHandlerResult.Resolved resolved || resolved.Payload.IsError)
            {
                return new DocumentResult(path, [], 0, FirstLine(ResultText(read)));
            }

            var text = StripLineNumbers(resolved.Payload.Text);
            var chunks = Chunk(text, _options.ChunkChars);
            if (chunks.Count > 1 && !allowLong)
            {
                // A reader that sees one slice of a long report cannot tell this year's statement from
                // last year's comparative or a segment table (mb1: 40-65 parts per question, 0.6).
                // The planner has Grep and Read for that; reading in parts is opt-in.
                return new DocumentResult(
                    path,
                    [],
                    0,
                    $"TOO LONG: {chunks.Count} parts of {_options.ChunkChars} chars. Grep for the line you need and Read around it, or pass allow_long=true to read it part by part.",
                    TooLong: true
                );
            }

            var records = new List<string>();
            for (var i = 0; i < chunks.Count; i++)
            {
                var label = chunks.Count == 1 ? path : $"{path} (part {i + 1} of {chunks.Count})";
                var reply = await AskReaderAsync(label, chunks[i], instruction, ct).ConfigureAwait(false);
                if (reply is not null)
                {
                    records.Add(chunks.Count == 1 ? reply : $"part {i + 1}: {reply}");
                }
            }

            return new DocumentResult(path, records, chunks.Count, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "map_read: reading {Path} failed", path);
            return new DocumentResult(path, [], 0, ex.Message);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <summary>One reader call. Null when the reader abstained.</summary>
    private async Task<string?> AskReaderAsync(string label, string text, string instruction, CancellationToken ct)
    {
        // The instruction sits in the system turn so every document in the call shares one cached prefix.
        var messages = new IMessage[]
        {
            new TextMessage
            {
                Role = Role.System,
                Text = ReaderSystemPrompt + "\n\nInstruction from the planner:\n" + instruction,
            },
            new TextMessage { Role = Role.User, Text = $"Document: {label}\n\n{text}" },
        };
        var options = _readerOptions with { Functions = null, MaxToken = _options.MaxOutputTokens };
        var reply = (await _reader.GenerateReplyAsync(messages, options, ct).ConfigureAwait(false)).ToList();

        foreach (var usage in reply.OfType<UsageMessage>())
        {
            // A plain provider call carries no generation id, so give each reader call its own: two
            // calls with the same token counts must not collapse into one usage record.
            var stamped = usage.GenerationId is null
                ? usage with
                {
                    GenerationId = Guid.NewGuid().ToString("N"),
                }
                : usage;
            _usage.RecordUsage(
                UsageRecordMapper.FromUsageMessage(
                    stamped,
                    _executorThreadId,
                    UsageExecutionKind.Executor,
                    _readerOptions.ModelId
                )
            );
        }

        var answer = string.Concat(
                reply
                    .Where(m => m is not (UsageMessage or TextMessage { IsThinking: true }))
                    .OfType<ICanGetText>()
                    .Select(m => m.GetText())
            )
            .Trim()
            .Trim('`', '*', '"')
            .Trim();
        if (answer.Length == 0)
        {
            throw new InvalidOperationException(
                "the reader returned no text (its output budget may have gone to reasoning)"
            );
        }

        return
            answer.StartsWith(AbstainMarker, StringComparison.OrdinalIgnoreCase)
            && answer.Length < AbstainMarker.Length + 4
            ? null
            : FirstLine(answer);
    }

    private static string FirstLine(string s)
    {
        var text = s.Trim();
        var cut = text.IndexOf('\n');
        return cut < 0 ? text : text[..cut].TrimEnd() + " [..]";
    }

    private static string ResultText(ToolHandlerResult result) =>
        result is ToolHandlerResult.Resolved r ? r.Payload.Text : "deferred";

    /// <summary>Drops a Read tool's line-number gutter when most lines carry one.</summary>
    internal static string StripLineNumbers(string text)
    {
        var lines = text.Split('\n');
        var numbered = lines.Count(LineNumberPrefix.IsMatch);
        var nonEmpty = lines.Count(l => l.Trim().Length > 0);
        if (nonEmpty == 0 || numbered < nonEmpty * 0.8)
        {
            return text;
        }

        return string.Join('\n', lines.Select(l => LineNumberPrefix.Replace(l, string.Empty, 1)));
    }

    /// <summary>Splits at line ends so no part is longer than <paramref name="maxChars"/> unless one line is.</summary>
    internal static List<string> Chunk(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return [text];
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > maxChars)
            {
                parts.Add(current.ToString());
                _ = current.Clear();
            }

            if (current.Length > 0)
            {
                _ = current.Append('\n');
            }

            _ = current.Append(line);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
