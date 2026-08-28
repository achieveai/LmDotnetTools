using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>How the work-item lookup ended. Three of these are DIFFERENT statements about the pull request
/// and the fourth is a statement about the daemon; collapsing any pair of them is the defect this enum
/// exists to prevent.</summary>
internal enum AdoWorkItemLookup
{
    /// <summary>
    /// Nothing was ever asked — no ADO project on the run, or no reader wired at all (a GitHub daemon).
    /// Deliberately the default (value 0) so a record nobody filled in renders NO block, rather than
    /// defaulting into <see cref="NoneLinked"/>, which is the one arm that would tell the reviewer a false
    /// fact about the pull request ("it links nothing") on the strength of nobody having looked.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The lookup was attempted and could not be completed — denied, non-success, or a shape this parser
    /// cannot read. Distinct from <see cref="NoneLinked"/> and rendered differently on purpose: "this PR has
    /// no work item" and "we could not read this PR's work items" license opposite reviewer behaviour, and a
    /// brief that renders them identically converts "I could not tell" into "I checked".
    /// </summary>
    Failed,

    /// <summary>The PR was read and links no work item at all. A positive finding, not an absence of one.</summary>
    NoneLinked,

    /// <summary>At least one linked work item was read.</summary>
    Linked,
}

/// <summary>
/// One work item on the PR's ancestry, flattened. <see cref="ParentId"/> and <see cref="Depth"/> carry the
/// shape of the chain without nesting the records, so the walk that built it and the block that renders it
/// share one representation.
/// </summary>
internal sealed record AdoWorkItem
{
    /// <summary>The ADO work item id.</summary>
    public required int Id { get; init; }

    /// <summary><c>System.WorkItemType</c> — <c>Bug</c>, <c>Task</c>, <c>User Story</c>, <c>Feature</c>,
    /// <c>Epic</c>. <c>null</c> when the payload did not carry it.</summary>
    public string? WorkItemType { get; init; }

    /// <summary><c>System.Title</c>. The author's words, so it reaches the BRIEF but never a log line — the
    /// same rule the executor already applies to the PR title and description.</summary>
    public string? Title { get; init; }

    /// <summary><c>System.State</c> — <c>Active</c>, <c>Resolved</c>, …</summary>
    public string? State { get; init; }

    /// <summary>The parent this item hangs off, via <c>System.LinkTypes.Hierarchy-Reverse</c>, or
    /// <c>null</c> at the top of the chain (or where the cap stopped the walk).</summary>
    public int? ParentId { get; init; }

    /// <summary>Hops above the PR's own linked items: 0 for an item the PR links directly, 1 for its parent,
    /// and so on.</summary>
    public required int Depth { get; init; }
}

/// <summary>
/// What the pull request was asked to do, as far as the daemon could establish it. Bounded so it can be
/// dropped straight into a review brief.
/// </summary>
internal sealed record AdoWorkItemContext
{
    /// <summary>The outcome. See <see cref="AdoWorkItemLookup"/>.</summary>
    public required AdoWorkItemLookup Outcome { get; init; }

    /// <summary>The items read, ordered by <see cref="AdoWorkItem.Depth"/> then id.</summary>
    public IReadOnlyList<AdoWorkItem> Items { get; init; } = [];

    /// <summary>
    /// How many items the total cap refused to fetch. Reported rather than silently elided, on the same rule
    /// every other capped block in the brief follows: a cut list reads to the reviewer as the complete set.
    /// </summary>
    public int OmittedItems { get; init; }

    /// <summary>Whether the depth cap stopped the walk before it ran out of parents — so a chain that is
    /// reported as ending at a Feature can say it might not really end there.</summary>
    public bool DepthCapReached { get; init; }

    /// <summary>Nobody looked. Renders no block at all.</summary>
    public static readonly AdoWorkItemContext Unavailable = new() { Outcome = AdoWorkItemLookup.Unavailable };

    /// <summary>Somebody looked and could not read the answer. Renders an explicit failure marker.</summary>
    public static readonly AdoWorkItemContext Failed = new() { Outcome = AdoWorkItemLookup.Failed };

    /// <summary>The PR genuinely links nothing. Renders an explicit "none linked" statement.</summary>
    public static readonly AdoWorkItemContext NoneLinked = new() { Outcome = AdoWorkItemLookup.NoneLinked };
}

/// <summary>
/// Reads a pull request's linked work items — and their parent chain up to the Epic — from Azure DevOps, so
/// the review brief can state what the change was actually asked to do.
/// <para>
/// It exists because the reviewer could not answer "does this diff accomplish what was asked". The capability
/// was offered to the model in the prompt, which told it to dispatch a context gatherer; across 644 observed
/// review sub-agent spawns ZERO carried a tool that can reach ADO, so the one time in 698 spawns it was
/// dispatched it had nothing to do the job with. The remedy is not a better prompt: the daemon fetches this
/// itself, in code, before the reviewer is ever asked a question, and hands over the answer.
/// </para>
/// <para>
/// Two route shapes. The PR's own links come from
/// <c>_apis/git/repositories/{repo}/pullRequests/{id}/workitems</c>, which already sits UNDER the run's
/// repository route and so needs no new policy surface at all; the items themselves come from the batch
/// <c>_apis/wit/workitems?ids=…&amp;$expand=relations</c>, which is project-scoped and is the single new route
/// root this reader required. Requests go through the injected policy-enforced <see cref="HttpClient"/> tagged
/// <see cref="SandboxOperation.ReadProviderMetadata"/>, exactly as <c>AdoPrProvider</c> does, so the same
/// per-run <see cref="OperationPolicy"/> that confines that one confines this.
/// </para>
/// <para>
/// Nothing here throws for a failed read. Losing the work items costs the brief a block; letting the failure
/// escape would cost the PR its review — the trade <c>AdoPrProvider</c>'s metadata lookups already make. A
/// failed read is, however, RENDERED rather than dropped: see <see cref="AdoWorkItemLookup.Failed"/>.
/// </para>
/// </summary>
internal sealed class AdoWorkItemContextReader
{
    private const string BaseUrl = "https://dev.azure.com";
    private const string ApiVersion = "7.1";

    /// <summary>
    /// How many parent hops the walk takes above the PR's own linked items. Four covers the deepest chain ADO
    /// models — Bug/Task → User Story → Feature → Epic is three — with one hop of slack for a process that
    /// nests one level further. It is a TERMINATION bound as much as a size one: a hierarchy that cycles
    /// cannot be walked forever, and de-duplication alone would not bound a chain that merely happens to be
    /// very long.
    /// </summary>
    public const int MaxAncestorDepth = 4;

    /// <summary>
    /// Hard cap on how many work items are fetched in total, across every depth. A PR can link a dozen items
    /// and each can carry its own chain; the brief has room for what the change was asked to do, not for a
    /// backlog. The overflow is COUNTED and reported, never silently dropped.
    /// </summary>
    public const int MaxWorkItems = 20;

    /// <summary>Cap on each title carried into the brief. A work item title is usually a line; nothing stops
    /// it being a paragraph, and one of those would crowd out the rest of the chain.</summary>
    public const int MaxTitleChars = 200;

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<AdoWorkItemContextReader> _logger;

    public AdoWorkItemContextReader(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<AdoWorkItemContextReader> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads the work items linked to one pull request, plus their ancestry.
    /// </summary>
    /// <param name="repo">The run's repository identity; supplies the org, project and repo the routes are built from.</param>
    /// <param name="prId">The pull request id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The context. <see cref="AdoWorkItemContext.Unavailable"/> when there was nothing to ask,
    /// <see cref="AdoWorkItemContext.Failed"/> when the ask could not be completed, and never an exception for
    /// a failed read.
    /// </returns>
    public async Task<AdoWorkItemContext> ReadAsync(
        RepoIdentity repo,
        string prId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrEmpty(prId);

        if (string.IsNullOrEmpty(repo.Project) || string.IsNullOrEmpty(repo.RepoName))
        {
            // Both routes below are /{org}/{project}/_apis/…, and the operation policy's work-item exception is
            // likewise built per project. Without one there is nothing to ask and nothing that would be
            // allowed. Unavailable rather than Failed on purpose: nobody attempted anything, so the brief says
            // nothing rather than reporting a failure that never happened.
            return AdoWorkItemContext.Unavailable;
        }

        try
        {
            var linkedIds = await ReadLinkedWorkItemIdsAsync(repo, prId, cancellationToken).ConfigureAwait(false);
            if (linkedIds is null)
            {
                return AdoWorkItemContext.Failed;
            }

            if (linkedIds.Count == 0)
            {
                _logger.LogDebug(
                    "ADO PR {PrId} on {Org}/{Project} links no work item; the brief says so explicitly.",
                    prId, repo.OrgOrOwner, repo.Project);
                return AdoWorkItemContext.NoneLinked;
            }

            var context = await WalkAncestryAsync(repo, prId, linkedIds, cancellationToken).ConfigureAwait(false);

            // Ids, types and counts only — NEVER titles. A work item title is the author's words, and the
            // executor already holds the PR title and description to the same rule.
            _logger.LogDebug(
                "Read ADO work items for {Org}/{Project} PR {PrId}: {Outcome}, {ItemCount} item(s) across "
                    + "{Depth} level(s) ({OmittedItems} over the cap, depth cap reached: {DepthCapReached}); "
                    + "types {Types}.",
                repo.OrgOrOwner,
                repo.Project,
                prId,
                context.Outcome,
                context.Items.Count,
                context.Items.Count == 0 ? 0 : context.Items.Max(static i => i.Depth) + 1,
                context.OmittedItems,
                context.DepthCapReached,
                string.Join(", ", context.Items.Select(static i => i.WorkItemType ?? "(untyped)").Distinct()));

            return context;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real caller cancellation (the review was abandoned) — propagate; nobody wants this result.
            throw;
        }
        catch (Exception ex)
        {
            // Everything else — an egress denial from the operation policy, an HttpClient TIMEOUT (which
            // surfaces as a TaskCanceledException even though the caller's token was NOT cancelled), a
            // malformed body — is a lookup that was ATTEMPTED and did not complete. That is Failed, not
            // Unavailable: the brief has to say the daemon could not read the work items, because the
            // alternative is a reviewer that reads silence as "this PR has none".
            _logger.LogDebug(
                ex,
                "ADO work-item read for {Org}/{Project} PR {PrId} failed; the brief will say the lookup failed.",
                repo.OrgOrOwner,
                repo.Project,
                prId);
            return AdoWorkItemContext.Failed;
        }
    }

    /// <summary>
    /// The ids the PR itself links, from
    /// <c>GET /{org}/{project}/_apis/git/repositories/{repo}/pullRequests/{prId}/workitems</c>. Returns
    /// <c>null</c> when the call could not be read at all — which is what separates "no work items" from
    /// "no answer" — and an empty list when the PR genuinely links none.
    /// </summary>
    private async Task<IReadOnlyList<int>?> ReadLinkedWorkItemIdsAsync(
        RepoIdentity repo,
        string prId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BaseUrl}/{repo.OrgOrOwner}/{repo.Project}/_apis/git/repositories/{repo.RepoName}"
            + $"/pullRequests/{prId}/workitems"
            + $"?api-version={ApiVersion}";

        using var document = await GetJsonAsync(url, "pull request work items", cancellationToken)
            .ConfigureAwait(false);
        if (document is null
            || !document.RootElement.TryGetProperty("value", out var refs)
            || refs.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (var reference in refs.EnumerateArray())
        {
            // The PR endpoint returns a ResourceRef whose id IS the work item id, sent as a STRING here and as
            // a number by the wit endpoint — hence one parser for both shapes.
            if (IdOf(reference, "id") is { } id && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Walks <c>System.LinkTypes.Hierarchy-Reverse</c> UPWARD from the PR's own items — Bug/Task → User Story
    /// → Feature → Epic — one batch request per level.
    /// <para>
    /// Bounded three ways, and all three are load-bearing. <see cref="MaxAncestorDepth"/> stops a very long
    /// chain; <see cref="MaxWorkItems"/> stops a wide one, counting what it refused rather than trimming
    /// quietly; and an explicit <c>seen</c> set means an id is fetched at most once, so a hierarchy that
    /// CYCLES (A parents B parents A — ADO does not forbid it, and a walk that assumes a tree hangs on one)
    /// terminates on the second visit instead of looping.
    /// </para>
    /// </summary>
    private async Task<AdoWorkItemContext> WalkAncestryAsync(
        RepoIdentity repo,
        string prId,
        IReadOnlyList<int> linkedIds,
        CancellationToken cancellationToken)
    {
        var items = new List<AdoWorkItem>();
        var seen = new HashSet<int>();
        var omitted = 0;
        var depthCapReached = false;

        // The frontier for the current level, already de-duplicated and already capped.
        var frontier = Admit(linkedIds, seen, items.Count, ref omitted);

        for (var depth = 0; frontier.Count > 0; depth++)
        {
            var batch = await ReadWorkItemsAsync(repo, frontier, cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                // A level that could not be read. Anything already collected is still true and still worth the
                // reviewer's attention, so the partial chain is kept; only a failure at the FIRST level leaves
                // nothing to report, and that is the one that becomes Failed.
                if (items.Count == 0)
                {
                    return AdoWorkItemContext.Failed;
                }

                break;
            }

            foreach (var parsed in batch)
            {
                items.Add(parsed.Item with { Depth = depth });
            }

            if (depth >= MaxAncestorDepth)
            {
                // Stop BEFORE requesting the next level. Whether that truncated a real chain is knowable: only
                // report the cap if something up there was actually waiting to be fetched.
                depthCapReached = batch.Any(static p => p.ParentId is not null);
                break;
            }

            var parents = batch
                .Select(static p => p.ParentId)
                .Where(static id => id is not null)
                .Select(static id => id!.Value)
                .ToList();

            frontier = Admit(parents, seen, items.Count, ref omitted);
            if (frontier.Count == 0 && parents.Count > 0 && omitted > 0)
            {
                // Parents existed but the total cap refused them: the chain is cut here, and the cut is
                // reported through OmittedItems rather than by pretending the chain ended.
                break;
            }
        }

        return new AdoWorkItemContext
        {
            Outcome = items.Count == 0 ? AdoWorkItemLookup.Failed : AdoWorkItemLookup.Linked,
            Items = [.. items.OrderBy(static i => i.Depth).ThenBy(static i => i.Id)],
            OmittedItems = omitted,
            DepthCapReached = depthCapReached,
        };
    }

    /// <summary>
    /// Admits as many of <paramref name="candidates"/> as the total cap allows, skipping anything already
    /// visited. The <c>seen</c> set is the cycle guard: an id enters it on admission and is never admitted
    /// twice, so a relation graph that loops back on itself contributes nothing on the second pass and the
    /// walk's frontier empties instead of repeating.
    /// </summary>
    private static List<int> Admit(
        IReadOnlyList<int> candidates,
        HashSet<int> seen,
        int alreadyCollected,
        ref int omitted)
    {
        var admitted = new List<int>();
        foreach (var id in candidates)
        {
            if (!seen.Add(id))
            {
                continue;
            }

            if (alreadyCollected + admitted.Count >= MaxWorkItems)
            {
                omitted++;
                continue;
            }

            admitted.Add(id);
        }

        return admitted;
    }

    /// <summary>
    /// One batch read of <c>GET /{org}/{project}/_apis/wit/workitems?ids={ids}&amp;$expand=relations</c>,
    /// parsed into items plus the parent each one names. Returns <c>null</c> when the level could not be read.
    /// </summary>
    private async Task<IReadOnlyList<(AdoWorkItem Item, int? ParentId)>?> ReadWorkItemsAsync(
        RepoIdentity repo,
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BaseUrl}/{repo.OrgOrOwner}/{repo.Project}/_apis/wit/workitems"
            + $"?ids={string.Join(",", ids.Select(static id => id.ToString(CultureInfo.InvariantCulture)))}"
            + "&$expand=relations"
            + $"&api-version={ApiVersion}";

        using var document = await GetJsonAsync(url, "work items", cancellationToken).ConfigureAwait(false);
        if (document is null
            || !document.RootElement.TryGetProperty("value", out var values)
            || values.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        var parsed = new List<(AdoWorkItem Item, int? ParentId)>();
        foreach (var value in values.EnumerateArray())
        {
            if (IdOf(value, "id") is not { } id)
            {
                continue;
            }

            var parentId = ParentIdOf(value);
            parsed.Add((
                new AdoWorkItem
                {
                    Id = id,
                    WorkItemType = FieldOf(value, "System.WorkItemType"),
                    Title = Condense(FieldOf(value, "System.Title")),
                    State = FieldOf(value, "System.State"),
                    ParentId = parentId,

                    // Overwritten by the caller, which is the only place that knows the level. Required
                    // members have to be set somewhere, and a wrong-but-unused 0 here would be a value the
                    // caller could forget to correct — so the caller's `with { Depth = depth }` is the single
                    // assignment that counts.
                    Depth = 0,
                },
                parentId));
        }

        return parsed;
    }

    /// <summary>
    /// The parent named by a work item's <c>System.LinkTypes.Hierarchy-Reverse</c> relation. REVERSE is the
    /// direction that points at the parent; <c>Hierarchy-Forward</c> on the same item lists its CHILDREN, and
    /// following that instead would walk down into sub-tasks and never reach the Epic that says what the work
    /// was for.
    /// <para>
    /// The id is the last segment of the relation's <c>url</c>
    /// (<c>https://…/_apis/wit/workItems/1200</c>) — ADO does not repeat it as a field.
    /// </para>
    /// </summary>
    private static int? ParentIdOf(JsonElement workItem)
    {
        if (!workItem.TryGetProperty("relations", out var relations)
            || relations.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        foreach (var relation in relations.EnumerateArray())
        {
            if (!string.Equals(
                    StringOf(relation, "rel"),
                    "System.LinkTypes.Hierarchy-Reverse",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (StringOf(relation, "url") is not { } url)
            {
                continue;
            }

            var lastSlash = url.LastIndexOf('/');
            var tail = lastSlash >= 0 && lastSlash < url.Length - 1 ? url[(lastSlash + 1)..] : url;
            if (int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>A <c>fields</c> entry as text, or <c>null</c> when absent or not a string.</summary>
    private static string? FieldOf(JsonElement workItem, string field) =>
        workItem.TryGetProperty("fields", out var fields) ? StringOf(fields, field) : null;

    /// <summary>
    /// Collapses a title to one truncated line. The line collapse is not cosmetic: these are rendered as list
    /// items in a brief whose structure the reviewer reads as fact, and a multi-line title would forge entries
    /// the daemon never wrote.
    /// </summary>
    private static string? Condense(string? title)
    {
        if (title is null)
        {
            return null;
        }

        var line = title.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxTitleChars ? line : line[..(MaxTitleChars - 1)] + "…";
    }

    /// <summary>An id property that ADO sends as a number on one route and a string on the other.</summary>
    private static int? IdOf(JsonElement element, string property)
    {
        if (element.ValueKind is not JsonValueKind.Object
            || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var id) => id,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) => id,
            _ => null,
        };
    }

    /// <summary>
    /// One authenticated GET, parsed. Returns <c>null</c> on a non-success status so each caller degrades to
    /// its own "could not establish" answer rather than throwing; transport and parse failures still throw and
    /// are caught once, in <see cref="ReadAsync"/>.
    /// <para>
    /// ADO authenticates REST with HTTP basic carrying the token in the PASSWORD field (the username is
    /// ignored), so the bearer is sent base64-encoded as <c>:{token}</c> — the same shape
    /// <c>AdoPrProvider</c> and <c>AdoReviewCommentPublisher</c> use. The token is fetched per request so an expiry
    /// mid-read refreshes rather than 401s.
    /// </para>
    /// </summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, string label, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url)
            .WithOperation(SandboxOperation.ReadProviderMetadata);
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug(
                "ADO work-item {Label} fetch returned {Status}; that part of the lookup stays unread.",
                label,
                (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>A direct string property, or <c>null</c> when absent, non-string, or blank.</summary>
    private static string? StringOf(JsonElement element, string property) =>
        element.ValueKind is JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
