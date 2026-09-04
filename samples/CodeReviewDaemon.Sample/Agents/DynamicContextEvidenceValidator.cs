using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>Builds the durable context manifest from semantic model output and host-observed evidence.</summary>
internal sealed class DynamicContextEvidenceValidator
{
    internal const string GathererTemplate = "code-reviewer:pr-context-gatherer";

    private static readonly JsonSerializerOptions MessageOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new IMessageJsonConverter() },
    };

    private static readonly HashSet<string> QualifiedReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Read",
        "Grep",
        "Glob",
        "mcp__github__get_issue",
        "mcp__github__get_pull_request",
        "mcp__github__get_pull_request_comments",
        "mcp__github__get_pull_request_files",
        "mcp__azure-devops__getPullRequest",
        "mcp__azure-devops__getPullRequestComments",
        "mcp__azure-devops__getPullRequestFileChanges",
        "mcp__azure-devops__getWorkItemById",
        "mcp__azure-devops__getWorkItemsBatch",
    };

    private const int MinimumCitationLength = 8;
    private static readonly string[] RequiredScopes = ["repository", "head", "workspace"];
    private readonly ReviewStore _store;

    public DynamicContextEvidenceValidator(ReviewStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public DynamicContextManifest Validate(
        DynamicContextGatheringResult result,
        DynamicContextBootstrap bootstrap,
        ReviewSubAgentTreeSnapshot settledRoster
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(settledRoster);

        var semantic = result.SemanticManifest;
        if (semantic.Version != DynamicContextManifest.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"The context manifest schema version {semantic.Version} is unsupported; expected {DynamicContextManifest.SchemaVersion}."
            );
        }

        if (semantic.EngagementRoundId != bootstrap.EngagementRoundId)
        {
            throw new InvalidOperationException("The context manifest names a different engagement round.");
        }

        if (string.IsNullOrWhiteSpace(result.ThreadId))
        {
            throw new InvalidOperationException("The context manifest has no parent conversation thread.");
        }

        var gatherers = settledRoster
            .Nodes.Where(node =>
                node.Status == ReviewSubAgentStatus.Completed
                && string.Equals(node.Template, GathererTemplate, StringComparison.Ordinal)
                && string.Equals(node.ParentThreadId, result.ThreadId, StringComparison.Ordinal)
            )
            .ToArray();
        if (gatherers.Length == 0)
        {
            throw new InvalidOperationException(
                $"Host evidence must contain a completed exact {GathererTemplate} descendant of the parent thread."
            );
        }

        var allAuditRecords = _store.ListAuditRecordsForRound(bootstrap.EngagementRoundId);
        var gatherer = gatherers
            .Select(node => new
            {
                Node = node,
                LastObservedAt = node.TerminalAtUtc
                    ?? allAuditRecords
                        .Where(record => string.Equals(record.ThreadId, node.ThreadId, StringComparison.Ordinal))
                        .Select(record => record.CompletedAtUtc ?? record.CapturedAtUtc)
                        .DefaultIfEmpty(DateTimeOffset.MinValue)
                        .Max(),
            })
            .OrderByDescending(candidate => candidate.LastObservedAt)
            .ThenByDescending(candidate => candidate.Node.AgentId, StringComparer.Ordinal)
            .Select(candidate => candidate.Node)
            .First();
        var auditRecords = allAuditRecords
            .Where(record =>
                record.CompletedAtUtc is not null
                && record.CaptureOutcome == AuditSourceCaptureOutcome.Complete
                && string.Equals(record.ThreadId, gatherer.ThreadId, StringComparison.Ordinal)
            )
            .ToArray();
        var calls = auditRecords
            .Where(record =>
                string.Equals(record.RecordType, MultiTurnAuditRecordTypes.ModelResponse, StringComparison.Ordinal)
            )
            .SelectMany(ReadToolCalls)
            .Where(call => !string.IsNullOrWhiteSpace(call.Message.ToolCallId))
            .GroupBy(
                call => new ToolCallKey(call.Record.RunId, call.Record.GenerationId, call.Message.ToolCallId!),
                ToolCallKeyComparer.Instance
            )
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(call => call.Record.Sequence).ToArray(),
                ToolCallKeyComparer.Instance
            );
        var reads = auditRecords
            .Where(record =>
                string.Equals(record.RecordType, MultiTurnAuditRecordTypes.ToolResult, StringComparison.Ordinal)
            )
            .Select(record => TryCreateQualifiedRead(record, calls, bootstrap))
            .Where(read => read is not null)
            .Select(read => read!)
            .GroupBy(read => read.CallKey, ToolCallKeyComparer.Instance)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(read => read.Record.Sequence)
            .ToArray();
        if (reads.Length < 2)
        {
            throw new InvalidOperationException(
                "Host evidence must contain at least two successful allow-listed scoped read tool results from the exact context gatherer."
            );
        }

        ValidateRequiredScopes(semantic.Gaps);

        var claims = semantic
            .Claims.Select(claim =>
            {
                if (
                    string.IsNullOrWhiteSpace(claim.ClaimId)
                    || string.IsNullOrWhiteSpace(claim.Text)
                    || claim.Citations.Count == 0
                    || claim.Citations.Any(citation =>
                        string.IsNullOrWhiteSpace(citation) || citation.Length < MinimumCitationLength
                    )
                )
                {
                    throw new InvalidOperationException("Every material context claim must carry a bounded citation.");
                }

                if (claim.Citations.Any(citation => !IsBoundedReference(citation)))
                {
                    throw new InvalidOperationException(
                        $"Context claim '{claim.ClaimId}' has a citation that is not a bounded provider, commit, file, or discussion reference."
                    );
                }

                var matchedCitations = claim
                    .Citations.Select(citation => new
                    {
                        Citation = citation,
                        Reads = reads.Where(read => CitationMatchesRead(citation, read, bootstrap)).ToArray(),
                    })
                    .ToArray();
                if (matchedCitations.FirstOrDefault(match => match.Reads.Length == 0) is { } unmatched)
                {
                    throw new InvalidOperationException(
                        $"Context claim '{claim.ClaimId}' citation '{unmatched.Citation}' is not backed by a matching immutable qualified source record."
                    );
                }

                var sources = matchedCitations
                    .SelectMany(match => match.Reads)
                    .DistinctBy(read => read.Record.Id, StringComparer.Ordinal)
                    .Select(read => new AuditSourceReference(read.Record.Id, read.Record.ContentSha256))
                    .ToArray();
                return new DynamicContextClaim(claim.ClaimId, claim.Text, claim.Citations, sources);
            })
            .ToArray();

        return new DynamicContextManifest(
            semantic.Version,
            semantic.EngagementRoundId,
            result.ThreadId,
            gatherer.AgentId,
            gatherer.Template,
            reads.Length,
            claims,
            semantic.Gaps
        );
    }

    internal static bool IsPersistedManifestValid(ReviewStore store, ReviewRun run, DynamicContextManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(manifest);
        if (
            run.EngagementRoundId is not { } roundId
            || manifest.Version != DynamicContextManifest.SchemaVersion
            || manifest.EngagementRoundId != roundId
            || manifest.ScopedReadCount < 2
            || manifest.Claims is null
            || manifest.Gaps is null
        )
        {
            return false;
        }

        try
        {
            ValidateRequiredScopes(manifest.Gaps);
            return manifest.Claims.All(claim =>
                claim.SourceRecordRefs is not null
                && claim.SourceRecordRefs.All(source => IsVerifiableManifestSource(store, roundId, source))
            );
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static void ValidateRequiredScopes(IReadOnlyList<DynamicContextGap> gaps)
    {
        foreach (var scope in RequiredScopes)
        {
            var matches = gaps.Where(gap => string.Equals(gap.Scope, scope, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1 || !matches[0].IsRequired || matches[0].State != DynamicContextGapState.Linked)
            {
                throw new InvalidOperationException(
                    $"The context manifest does not establish the required '{scope}' scope."
                );
            }
        }

        if (gaps.Any(gap => gap.IsRequired && gap.State != DynamicContextGapState.Linked))
        {
            throw new InvalidOperationException("The context manifest contains a failed required scope.");
        }
    }

    private static bool IsVerifiableManifestSource(ReviewStore store, long roundId, AuditSourceReference source)
    {
        if (
            string.IsNullOrWhiteSpace(source.SourceRecordId)
            || string.IsNullOrWhiteSpace(source.ContentSha256)
            || store.GetAuditRecord(source.SourceRecordId) is not { } record
            || record.EngagementRoundId != roundId
            || record.CaptureOutcome != AuditSourceCaptureOutcome.Complete
            || record.CompletedAtUtc is null
            || !string.Equals(record.ContentSha256, source.ContentSha256, StringComparison.Ordinal)
        )
        {
            return false;
        }

        _ = store.ReadAuditContent(record.Id);
        return true;
    }

    private IEnumerable<AuditedToolCall> ReadToolCalls(AuditSourceRecord record)
    {
        IMessage? message;
        try
        {
            var content = _store.ReadAuditContent(record.Id);
            message = JsonSerializer.Deserialize<IMessage>(content, MessageOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        switch (message)
        {
            case ToolCallMessage call:
                yield return new AuditedToolCall(record, call);
                break;
            case ToolsCallMessage calls:
                foreach (var toolCall in calls.ToolCalls)
                {
                    yield return new AuditedToolCall(record, toolCall);
                }

                break;
            default:
                yield break;
        }
    }

    private bool TryReadToolResult(AuditSourceRecord record, out ToolCallResultMessage? message)
    {
        message = null;
        try
        {
            var content = _store.ReadAuditContent(record.Id);
            message = JsonSerializer.Deserialize<IMessage>(content, MessageOptions) as ToolCallResultMessage;
            return message is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private QualifiedRead? TryCreateQualifiedRead(
        AuditSourceRecord record,
        IReadOnlyDictionary<ToolCallKey, AuditedToolCall[]> calls,
        DynamicContextBootstrap bootstrap
    )
    {
        if (!TryReadToolResult(record, out var result) || result is null)
        {
            return null;
        }

        return TryMatchScopedCall(record, result, calls, bootstrap, out var matched, out var key)
            ? new QualifiedRead(record, result, matched!, key!)
            : null;
    }

    private static bool TryMatchScopedCall(
        AuditSourceRecord resultRecord,
        ToolCallResultMessage result,
        IReadOnlyDictionary<ToolCallKey, AuditedToolCall[]> calls,
        DynamicContextBootstrap bootstrap,
        out AuditedToolCall? matched,
        out ToolCallKey? key
    )
    {
        matched = null;
        key = null;
        if (
            result.IsError
            || result.IsDeferred
            || string.IsNullOrWhiteSpace(result.ToolCallId)
            || string.IsNullOrWhiteSpace(result.ToolName)
            || !QualifiedReadTools.Contains(result.ToolName)
        )
        {
            return false;
        }

        key = new ToolCallKey(resultRecord.RunId, resultRecord.GenerationId, result.ToolCallId);
        if (!calls.TryGetValue(key, out var candidates))
        {
            return false;
        }

        var preceding = candidates
            .Where(call => call.Record.Sequence < resultRecord.Sequence)
            .OrderBy(call => call.Record.Sequence)
            .ToArray();
        if (preceding.Length != 1)
        {
            return false;
        }

        matched = preceding[0];
        return string.Equals(matched.Message.FunctionName, result.ToolName, StringComparison.OrdinalIgnoreCase)
            && IsCallInScope(matched.Message, bootstrap);
    }

    private static bool IsCallInScope(ToolCall call, DynamicContextBootstrap bootstrap)
    {
        if (
            string.IsNullOrWhiteSpace(call.FunctionName)
            || string.IsNullOrWhiteSpace(call.FunctionArgs)
            || !TryParseObject(call.FunctionArgs, out var args)
        )
        {
            return false;
        }

        using (args)
        {
            return call.FunctionName switch
            {
                "Read" => HasPathWithin(args.RootElement, "file_path", bootstrap.WorkspacePath),
                "Grep" or "Glob" => HasPathWithin(args.RootElement, "path", bootstrap.WorkspacePath),
                "mcp__github__get_pull_request"
                or "mcp__github__get_pull_request_comments"
                or "mcp__github__get_pull_request_files" => IsGitHubRepository(args.RootElement, bootstrap)
                    && HasExactScalar(args.RootElement, "pull_number", bootstrap.PrId),
                "mcp__github__get_issue" => IsAllowedGitHubIssue(args.RootElement, bootstrap.LinkedWorkItemRefs),
                "mcp__azure-devops__getPullRequest"
                or "mcp__azure-devops__getPullRequestComments"
                or "mcp__azure-devops__getPullRequestFileChanges" => IsAdoRepository(args.RootElement, bootstrap)
                    && HasExactScalar(args.RootElement, "pullRequestId", bootstrap.PrId),
                "mcp__azure-devops__getWorkItemById" => IsAdoBootstrap(bootstrap)
                    && HasAllowedWorkItem(args.RootElement, bootstrap.LinkedWorkItemRefs),
                "mcp__azure-devops__getWorkItemsBatch" => IsAdoBootstrap(bootstrap)
                    && HasOnlyAllowedWorkItems(args.RootElement, bootstrap.LinkedWorkItemRefs),
                _ => false,
            };
        }
    }

    private static bool HasPathWithin(JsonElement args, string propertyName, string workspacePath)
    {
        if (!TryGetString(args, propertyName, out var candidate) || string.IsNullOrWhiteSpace(workspacePath))
        {
            return false;
        }

        var comparison = IsWindowsRootedPath(workspacePath)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            !TryCanonicalizePath(workspacePath, comparison, out var root)
            || !TryCanonicalizePath(candidate, comparison, out var path)
        )
        {
            return false;
        }

        return string.Equals(path, root, comparison) || path.StartsWith(root + "/", comparison);
    }

    private static bool TryCanonicalizePath(string value, StringComparison comparison, out string canonical)
    {
        canonical = string.Empty;
        var path = NormalizePath(value);
        string root;
        string remainder;
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            var uncSegments = path[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uncSegments.Length < 2)
            {
                return false;
            }

            root = $"//{uncSegments[0]}/{uncSegments[1]}";
            remainder = string.Join('/', uncSegments.Skip(2));
        }
        else if (path.StartsWith("/", StringComparison.Ordinal))
        {
            root = string.Empty;
            remainder = path[1..];
        }
        else if (IsWindowsRootedPath(path))
        {
            root = char.ToUpperInvariant(path[0]) + ":";
            remainder = path[3..];
        }
        else
        {
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in remainder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        canonical = root.Length == 0 ? "/" + string.Join('/', segments) : root + "/" + string.Join('/', segments);
        if (root.StartsWith("//", StringComparison.Ordinal) && !canonical.StartsWith(root, comparison))
        {
            return false;
        }

        return true;
    }

    private static bool IsWindowsRootedPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '/' || path[2] == '\\');

    private static bool IsGitHubRepository(JsonElement args, DynamicContextBootstrap bootstrap)
    {
        var parts = bootstrap.RepoRef.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
            && string.Equals(parts[0], "github", StringComparison.OrdinalIgnoreCase)
            && TryGetString(args, "owner", out var owner)
            && TryGetString(args, "repo", out var repo)
            && string.Equals(owner, parts[1], StringComparison.OrdinalIgnoreCase)
            && string.Equals(repo, parts[2], StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdoBootstrap(DynamicContextBootstrap bootstrap) =>
        bootstrap.RepoRef.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var provider, _, _, _]
        && string.Equals(provider, "azure-devops", StringComparison.OrdinalIgnoreCase);

    private static bool IsAdoRepository(JsonElement args, DynamicContextBootstrap bootstrap)
    {
        var parts = bootstrap.RepoRef.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4
            && string.Equals(parts[0], "azure-devops", StringComparison.OrdinalIgnoreCase)
            && TryGetString(args, "project", out var project)
            && TryGetString(args, "repository", out var repository)
            && string.Equals(project, parts[2], StringComparison.OrdinalIgnoreCase)
            && string.Equals(repository, parts[3], StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExactScalar(JsonElement args, string name, string expected) =>
        TryGetScalar(args, [name], out var value) && string.Equals(value, expected, StringComparison.Ordinal);

    private static bool IsAllowedGitHubIssue(JsonElement args, IReadOnlyList<string> references)
    {
        if (
            !TryGetString(args, "owner", out var owner)
            || !TryGetString(args, "repo", out var repository)
            || !TryGetScalar(args, ["issue_number"], out var id)
        )
        {
            return false;
        }

        var expected = $"{owner}/{repository}#{id}";
        return references.Any(reference =>
            reference.StartsWith("github-issue:", StringComparison.OrdinalIgnoreCase)
            && string.Equals(reference["github-issue:".Length..], expected, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static bool HasAllowedWorkItem(JsonElement args, IReadOnlyList<string> references) =>
        TryGetScalar(args, ["id"], out var id)
        && references.Any(reference => ReferenceHasId(reference, "ado-work-item", id));

    private static bool HasOnlyAllowedWorkItems(JsonElement args, IReadOnlyList<string> references)
    {
        if (!TryGetProperty(args, "ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var requested = ids.EnumerateArray().Select(ScalarText).Where(id => id is not null).ToArray();
        return requested.Length > 0
            && requested.Length == ids.GetArrayLength()
            && requested.All(id => references.Any(reference => ReferenceHasId(reference, "ado-work-item", id!)));
    }

    private static bool ReferenceHasId(string reference, string kind, string id)
    {
        var prefix = kind + ":";
        if (!reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = reference[prefix.Length..];
        return string.Equals(value, id, StringComparison.OrdinalIgnoreCase)
            || value.EndsWith($"#{id}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseObject(string json, out JsonDocument document)
    {
        document = null!;
        try
        {
            document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            document.Dispose();
            document = null!;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static bool TryGetScalar(JsonElement element, IReadOnlyList<string> names, out string value)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var property) && ScalarText(property) is { } scalar)
            {
                value = scalar;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (
                !string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(property.Name, name, StringComparison.Ordinal)
                || found
            )
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                continue;
            }

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static string? ScalarText(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };

    private static bool IsBoundedReference(string citation) =>
        citation.StartsWith("file:", StringComparison.Ordinal)
        || citation.StartsWith("issue:", StringComparison.Ordinal)
        || citation.StartsWith("workitem:", StringComparison.Ordinal)
        || citation.StartsWith("pull-request:", StringComparison.Ordinal)
        || citation.StartsWith("commit:", StringComparison.Ordinal)
        || citation.StartsWith("discussion:", StringComparison.Ordinal);

    private static bool CitationMatchesRead(string citation, QualifiedRead read, DynamicContextBootstrap bootstrap) =>
        ContainsOrdinal(read.Message.Result, citation) && CitationMatchesCall(citation, read.Call.Message, bootstrap);

    private static bool CitationMatchesCall(string citation, ToolCall call, DynamicContextBootstrap bootstrap)
    {
        if (!TryParseObject(call.FunctionArgs ?? string.Empty, out var args))
        {
            return false;
        }

        using (args)
        {
            if (citation.StartsWith("file:", StringComparison.Ordinal))
            {
                var relativePath = citation["file:".Length..];
                var expectedPath = NormalizePath(bootstrap.WorkspacePath) + "/" + NormalizePath(relativePath);
                var property = string.Equals(call.FunctionName, "Read", StringComparison.Ordinal)
                    ? "file_path"
                    : "path";
                return HasExactCanonicalPath(args.RootElement, property, expectedPath, bootstrap.WorkspacePath);
            }

            if (citation.StartsWith("issue:", StringComparison.Ordinal))
            {
                var id = citation["issue:".Length..];
                return string.Equals(call.FunctionName, "mcp__github__get_issue", StringComparison.Ordinal)
                    && TryGetScalar(args.RootElement, ["issue_number"], out var observed)
                    && string.Equals(observed, id, StringComparison.Ordinal)
                    && IsAllowedGitHubIssue(args.RootElement, bootstrap.LinkedWorkItemRefs);
            }

            if (citation.StartsWith("workitem:", StringComparison.Ordinal))
            {
                var id = citation["workitem:".Length..];
                return string.Equals(call.FunctionName, "mcp__azure-devops__getWorkItemById", StringComparison.Ordinal)
                    && TryGetScalar(args.RootElement, ["id"], out var observed)
                    && string.Equals(observed, id, StringComparison.Ordinal)
                    && HasAllowedWorkItem(args.RootElement, bootstrap.LinkedWorkItemRefs);
            }

            if (citation.StartsWith("pull-request:", StringComparison.Ordinal))
            {
                var id = citation["pull-request:".Length..];
                return call.FunctionName switch
                {
                    "mcp__github__get_pull_request"
                    or "mcp__github__get_pull_request_comments"
                    or "mcp__github__get_pull_request_files" => IsGitHubRepository(args.RootElement, bootstrap)
                        && TryGetScalar(args.RootElement, ["pull_number"], out var observed)
                        && string.Equals(observed, id, StringComparison.Ordinal),
                    "mcp__azure-devops__getPullRequest"
                    or "mcp__azure-devops__getPullRequestComments"
                    or "mcp__azure-devops__getPullRequestFileChanges" => IsAdoRepository(args.RootElement, bootstrap)
                        && TryGetScalar(args.RootElement, ["pullRequestId"], out var observed)
                        && string.Equals(observed, id, StringComparison.Ordinal),
                    _ => false,
                };
            }

            if (citation.StartsWith("commit:", StringComparison.Ordinal))
            {
                var sha = citation["commit:".Length..];
                return string.Equals(sha, bootstrap.BaseSha, StringComparison.Ordinal)
                    || string.Equals(sha, bootstrap.HeadSha, StringComparison.Ordinal)
                    || string.Equals(sha, bootstrap.MergeBaseSha, StringComparison.Ordinal);
            }

            if (citation.StartsWith("discussion:", StringComparison.Ordinal))
            {
                return bootstrap.DiscussionRefs.Any(reference =>
                    string.Equals(reference, citation, StringComparison.Ordinal)
                );
            }

            return false;
        }
    }

    private static bool HasExactCanonicalPath(
        JsonElement args,
        string propertyName,
        string expectedPath,
        string workspacePath
    )
    {
        if (!TryGetString(args, propertyName, out var candidate))
        {
            return false;
        }

        var comparison = IsWindowsRootedPath(workspacePath)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return TryCanonicalizePath(candidate, comparison, out var actual)
            && TryCanonicalizePath(expectedPath, comparison, out var expected)
            && string.Equals(actual, expected, comparison);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static bool ContainsOrdinal(string value, string citation) =>
        value.Contains(citation, StringComparison.Ordinal);

    private sealed record QualifiedRead(
        AuditSourceRecord Record,
        ToolCallResultMessage Message,
        AuditedToolCall Call,
        ToolCallKey CallKey
    );

    private sealed record AuditedToolCall(AuditSourceRecord Record, ToolCall Message);

    private sealed record ToolCallKey(string RunId, string GenerationId, string ToolCallId);

    private sealed class ToolCallKeyComparer : IEqualityComparer<ToolCallKey>
    {
        public static ToolCallKeyComparer Instance { get; } = new();

        public bool Equals(ToolCallKey? x, ToolCallKey? y) =>
            x is not null
            && y is not null
            && string.Equals(x.RunId, y.RunId, StringComparison.Ordinal)
            && string.Equals(x.GenerationId, y.GenerationId, StringComparison.Ordinal)
            && string.Equals(x.ToolCallId, y.ToolCallId, StringComparison.Ordinal);

        public int GetHashCode(ToolCallKey obj) => HashCode.Combine(obj.RunId, obj.GenerationId, obj.ToolCallId);
    }
}
