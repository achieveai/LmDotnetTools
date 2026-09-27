using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmAgentInfra.Security;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The run identity every exported file is bound to (task #82, requirement 2). Stamped into each JSON file
/// when it is built and re-checked when the cached bundle is read back, so a file cannot be replayed under a
/// different run, a different PR or a different branch than the one it describes.
/// <para>
/// This is what makes the retention bundle's own <c>ReviewRunId</c> check non-trivial. That check proves the
/// bundle envelope names this run; without a per-file binding, the FILES inside it could have been lifted
/// wholesale from another run's bundle and the envelope rewritten. Now every file carries the claim.
/// </para>
/// </summary>
/// <param name="ReviewRunId">The run whose output this is.</param>
/// <param name="PrId">The pull request the run reviewed.</param>
/// <param name="RepoKey">The reviewed repository's normalized key.</param>
/// <param name="BaseSha">The base the review was frozen against.</param>
/// <param name="HeadSha">The head the review was frozen against.</param>
/// <param name="ArtifactBranch">The branch these files are retained on.</param>
internal sealed record ReviewArtifactExportBinding(
    long ReviewRunId,
    string PrId,
    string RepoKey,
    string BaseSha,
    string HeadSha,
    string ArtifactBranch
)
{
    public static ReviewArtifactExportBinding Build(ReviewRun run, RepoIdentity repo, string artifactBranch)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactBranch);
        return new ReviewArtifactExportBinding(
            run.Id,
            run.PrId,
            repo.NormalizedKey,
            run.BaseSha ?? string.Empty,
            run.HeadSha,
            artifactBranch
        );
    }

    /// <summary>
    /// The binding as it is written into a file. Property order is fixed by this one method, which is what
    /// lets validation compare the serialized forms rather than walking the tree.
    /// </summary>
    public JsonObject ToJson() =>
        new()
        {
            ["ReviewRunId"] = ReviewRunId,
            ["PrId"] = PrId,
            ["RepoKey"] = RepoKey,
            ["BaseSha"] = BaseSha,
            ["HeadSha"] = HeadSha,
            ["ArtifactBranch"] = ArtifactBranch,
        };

    public string ToCanonicalString() => ToJson().ToJsonString();
}

/// <summary>
/// Task #82, requirement 2 — turns one run's already-validated review output into the files that sit on its
/// artifact branch, under the pilot capability only.
/// <para>
/// It DERIVES; it does not re-decide. Every value here comes from the same
/// <c>Canonical</c>/<c>Admission</c> nodes <c>WorkflowOperationDispatcher.PersistCanonical</c> writes to the
/// private store. There is no second parse of the review text, no second findings model, and no path by
/// which the branch and the store can disagree about what the review said.
/// </para>
/// <para>
/// <b>Two classes of field, two different answers.</b> Daemon-authored structured values — the binding,
/// the identities, the receipt inventory, the route and SHAs — are produced by this codebase from its own
/// records and must NEVER contain a credential; if one appears there, something is badly wrong and the
/// export aborts. Untrusted free text — the review prose, finding descriptions, grade and publication
/// narrative, and the existing PR discussion — is written by a language model or by third parties on the
/// pull request, and a credential-shaped string in it is an ordinary, expected event. Aborting the whole
/// bundle over a reviewer who pasted a JWT into a comment would mean the daemon silently stops retaining
/// anything for that PR, which is the opposite of capturing the review. Those fields are REDACTED in
/// place instead, each match replaced by <c>[REDACTED:&lt;shape&gt;]</c>.
/// </para>
/// <para>
/// Redaction applies to the branch export ONLY. The private SQLite artifacts keep the original bytes:
/// they are the daemon's record of what was actually said and reviewed, they are not published, and
/// rewriting them would destroy evidence to protect a channel they never travel on.
/// </para>
/// <para>
/// <b>The order matters and it is what keeps the gate honest.</b> Free text is redacted first, then every
/// assembled file — trusted and untrusted alike — goes through the same fail-closed
/// <see cref="ValidateNoSecrets(ReviewArtifactFile)"/>. Because redaction uses the same pattern set, a
/// redacted field can no longer match, so the universal gate can now only fire on a trusted field. There
/// is no per-file exemption to get wrong, no list of "files we skip scanning", and adding a new trusted
/// field to the export automatically inherits fail-closed behaviour.
/// </para>
/// <para>
/// A scan or a redaction that cannot COMPLETE still aborts, always. Partially-redacted text is
/// indistinguishable from clean text.
/// </para>
/// <para>
/// One thing is deliberately NOT exported: the reviewed diff body. <c>context.json</c> carries the frozen
/// context's IDENTITY (PR, base, head, route) plus a digest and length of the diff, which is what makes the
/// export reproducible, and says so in a field an operator will read. Copying the source of a pull request
/// into the artifact repository is a confidentiality expansion nobody asked for and it is not review OUTPUT.
/// The private <c>review-context</c> artifact keeps the body.
/// </para>
/// </summary>
internal static class ReviewArtifactExport
{
    public const int SchemaVersion = 1;

    /// <summary>The operator-facing statement of what <c>context.json</c> does and does not carry.</summary>
    internal const string DiffBodyNotice =
        "The reviewed diff is identified here by SHA-256 digest, byte length and merge base ONLY. Its BODY "
        + "is deliberately not retained on this branch: the pull request's source is not review output, and "
        + "publishing it here would widen who can read it. To reproduce this review, fetch the diff between "
        + "BaseSha and HeadSha from the source repository and check it against Diff.Sha256. The daemon's "
        + "private 'review-context' artifact holds the body it actually used.";

    /// <summary>The complete, closed set of names the export may add next to <c>summary.json</c>.</summary>
    private static readonly string[] FileNames =
    [
        "review.md",
        "grade.md",
        "comments.md",
        "performance.md",
        "discussion.md",
        "setup.md",
        "findings.json",
        "grade.json",
        "context.json",
        "comments.json",
        "discussion.json",
        "publication.json",
        "identities.json",
        "receipts.json",
        "review-notes.json",
    ];

    public static bool IsExportFileName(string fileName) => FileNames.Contains(fileName, StringComparer.Ordinal);

    public static ReviewArtifactFile BuildReviewNotes(
        string prefix,
        ReviewArtifactExportBinding binding,
        JsonArray notes
    )
    {
        var path = prefix + "review-notes.json";
        var file = Json(path, binding, new JsonObject { ["Files"] = Redact(path, notes) });
        ValidateNoSecrets(file);
        return file;
    }

    /// <summary>
    /// Builds the export for one run. <paramref name="prefix"/> is the per-instance directory the summary
    /// already lives in, so the whole export is scoped to the same PR-and-instance path the retention
    /// allow-list validates.
    /// </summary>
    /// <param name="prefix">The per-instance directory the summary already lives in.</param>
    /// <param name="run">The run whose output is being exported.</param>
    /// <param name="repo">The reviewed repository's identity.</param>
    /// <param name="artifactBranch">The branch these files are retained on.</param>
    /// <param name="admission">The frozen admission (route, PR, head) the run was bound to.</param>
    /// <param name="frozenContext">
    /// The frozen PR context: the pull request's own identity fields and the existing discussion the review
    /// was shown. Exported as DATA — a transcript of what the reviewer read — which is a read of the source
    /// PR that already happened, not a write to it. Nothing in this class can reach a provider.
    /// </param>
    /// <param name="canonical">
    /// The workflow's validated <c>Canonical</c> node (review, grade, publication). Null on a route that has
    /// none, which yields no files at all rather than empty ones.
    /// </param>
    /// <param name="inventory">
    /// The retention-INDEPENDENT artifact/receipt inventory from
    /// <c>WorkflowArtifactOperations.CollectRetentionInventory</c>. Counts only, and counts that do not move
    /// depending on when they were taken — see that method for why the ordinary statistics cannot be used
    /// here. There is no idempotency key, provider response id, header, environment value or model
    /// credential in it, so there is nothing in <c>receipts.json</c> to redact.
    /// </param>
    /// <param name="diffDigest">Digest and length of the diff this review was frozen against, if any.</param>
    public static IReadOnlyList<ReviewArtifactFile> Build(
        string prefix,
        ReviewRun run,
        RepoIdentity repo,
        string artifactBranch,
        JsonObject admission,
        JsonObject frozenContext,
        JsonObject? canonical,
        JsonObject inventory,
        JsonObject? diffDigest
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(frozenContext);
        ArgumentNullException.ThrowIfNull(inventory);
        // The route guard lives HERE, not at the call site, so no caller can forget it. Only `new_head`
        // produces a canonical review that `PersistCanonical` accepts; on `merged` or `discussion` the
        // private store holds no review, grade or findings for this run, so a Canonical node arriving in
        // the operation input is either malformed or someone else's. Exporting it would publish content
        // the daemon never validated and never recorded. Absence of a route-appropriate canonical yields
        // no files at all rather than empty ones.
        if (canonical is null || admission["Route"]?.GetValue<string>() != "new_head")
        {
            return [];
        }

        var binding = ReviewArtifactExportBinding.Build(run, repo, artifactBranch);
        var review =
            canonical["Review"] as JsonObject
            ?? throw new InvalidOperationException("The review export requires a canonical review.");
        var grade =
            canonical["Grade"] as JsonObject
            ?? throw new InvalidOperationException("The review export requires a canonical grade.");
        if (review["Format"]?.GetValue<string>() == "markdown")
            return BuildMarkdown(prefix, run, repo, binding, admission, canonical, inventory, diffDigest);
        var findings =
            review["Findings"] as JsonArray
            ?? throw new InvalidOperationException("The review export requires canonical findings.");

        // A collect-only run publishes nothing, and the workflow records that as an explicit no-op rather
        // than as an absence — so the export says so in as many words instead of omitting the file.
        var publication =
            canonical["Publication"]?.DeepClone() as JsonObject
            ?? new JsonObject { ["Outcome"] = "no_op", ["Actions"] = new JsonArray() };

        // Everything below this line that came from a model or from a third party on the pull request is
        // redacted BEFORE assembly. The originals stay in the private store; only the branch copy changes.
        var reviewText = Redact(prefix + "review.md", review["ReviewText"]?.GetValue<string>() ?? string.Empty);
        var redactedFindings = Redact(prefix + "findings.json", findings);
        var redactedGrade = Redact(prefix + "grade.json", grade);
        var redactedPublication = Redact(prefix + "publication.json", publication);
        var redactedDiscussion = Redact(prefix + "discussion.json", frozenContext);

        var files = new List<ReviewArtifactFile>
        {
            // Prose, so the binding cannot be a JSON property. It is a trailer instead — a fixed marker
            // plus the same canonical binding string every other file carries, appended last so a forged
            // trailer inside the review text cannot be the one that is checked.
            new(prefix + "review.md", reviewText + BindingTrailer(binding)),
            Json(prefix + "findings.json", binding, new JsonObject { ["Findings"] = redactedFindings }),
            Json(
                prefix + "grade.json",
                binding,
                new JsonObject
                {
                    ["GradeKind"] = "per-finding-support",
                    ["Description"] = redactedGrade["Description"]?.DeepClone(),
                    ["Assessments"] = redactedGrade["Assessments"]?.DeepClone() ?? new JsonArray(),
                }
            ),
            Json(
                prefix + "publication.json",
                binding,
                new JsonObject
                {
                    ["Outcome"] = redactedPublication["Outcome"]?.DeepClone() ?? "no_op",
                    ["Actions"] = redactedPublication["Actions"]?.DeepClone() ?? new JsonArray(),
                    ["Description"] = redactedPublication["Description"]?.DeepClone(),
                }
            ),
            // The PROPOSED comments, as text. These are the draft actions the publication step either
            // performed or — on a collect-only run — explicitly declined, recorded here so a reviewer can
            // read what would have been said. Writing this file is not posting it, and there is no code path
            // from a retained artifact back to the provider.
            Json(
                prefix + "comments.json",
                binding,
                new JsonObject
                {
                    ["Posted"] = !string.Equals(
                        publication["Outcome"]?.GetValue<string>(),
                        "no_op",
                        StringComparison.Ordinal
                    ),
                    ["Proposed"] = redactedPublication["Actions"]?.DeepClone() ?? new JsonArray(),
                    ["Draft"] = reviewText,
                }
            ),
            // The existing PR discussion exactly as the review was shown it — an immutable transcript of a
            // read, kept so the review can be re-read against the conversation it actually answered. It is
            // written by third parties, so it is the single most likely place for a pasted credential and
            // the least acceptable place to abort the whole retention over one.
            Json(
                prefix + "discussion.json",
                binding,
                new JsonObject
                {
                    ["PullRequest"] = redactedDiscussion["PullRequest"]?.DeepClone(),
                    ["CommentBaseline"] = redactedDiscussion["CommentBaseline"]?.DeepClone() ?? new JsonArray(),
                    ["CommentWindow"] = redactedDiscussion["CommentWindow"]?.DeepClone() ?? new JsonArray(),
                }
            ),
            Json(
                prefix + "context.json",
                binding,
                new JsonObject
                {
                    ["Route"] = admission["Route"]?.DeepClone(),
                    ["PrId"] = run.PrId,
                    ["BaseSha"] = run.BaseSha,
                    ["HeadSha"] = run.HeadSha,
                    ["Diff"] = diffDigest?.DeepClone(),
                    // Stated as data, not only in a code comment: whoever reads this branch is the person
                    // who needs to know the source is not here and where to get it.
                    ["DiffBodyExported"] = false,
                    ["Notice"] = DiffBodyNotice,
                }
            ),
            Json(
                prefix + "identities.json",
                binding,
                new JsonObject
                {
                    ["Provider"] = repo.Provider,
                    ["RepoKey"] = repo.NormalizedKey,
                    ["RepoDisplayName"] = repo.DisplayName,
                    ["PrId"] = run.PrId,
                    ["ReviewRunId"] = run.Id,
                    ["ReviewKind"] = run.ReviewKind,
                    ["VariantId"] = run.VariantId,
                    ["Mode"] = run.Mode,
                    ["ArtifactBranch"] = artifactBranch,
                }
            ),
            Json(prefix + "receipts.json", binding, inventory.DeepClone().AsObject()),
        };

        foreach (var file in files)
        {
            ValidateNoSecrets(file);
        }
        return files;
    }

    private static IReadOnlyList<ReviewArtifactFile> BuildMarkdown(
        string prefix,
        ReviewRun run,
        RepoIdentity repo,
        ReviewArtifactExportBinding binding,
        JsonObject admission,
        JsonObject canonical,
        JsonObject inventory,
        JsonObject? diffDigest
    )
    {
        var files = new List<ReviewArtifactFile>();
        foreach (
            var (key, name) in new[]
            {
                ("Review", "review.md"),
                ("Grade", "grade.md"),
                ("Comments", "comments.md"),
                ("Performance", "performance.md"),
            }
        )
        {
            if (
                canonical[key]?["Format"]?.GetValue<string>() != "markdown"
                || canonical[key]?["Markdown"]?.GetValue<string>() is not { } text
                || string.IsNullOrWhiteSpace(text)
            )
                throw new InvalidDataException("A complete Markdown report is required for retention.");
            files.Add(new(prefix + name, Redact(prefix + name, text) + BindingTrailer(binding)));
        }
        var discussion =
            canonical["Discussion"] as JsonObject ?? throw new InvalidDataException("Deferred discussion is missing.");
        if (
            discussion["ReviewRunId"]?.GetValue<long>() != run.Id
            || discussion["HeadSha"]?.GetValue<string>() != run.HeadSha
        )
            throw new InvalidDataException("Deferred discussion identity does not match the review.");
        files.Add(
            new(
                prefix + "discussion.md",
                "# Discussion captured after review and grading\n\n"
                    + WorkflowMarkdown.YamlDocument(Redact(prefix + "discussion.md", discussion))
                    + BindingTrailer(binding)
            )
        );
        var setup = new JsonObject
        {
            ["Route"] = admission["Route"]?.DeepClone(),
            ["PrId"] = run.PrId,
            ["BaseSha"] = run.BaseSha,
            ["HeadSha"] = run.HeadSha,
            ["Diff"] = diffDigest?.DeepClone(),
            ["DiffBodyExported"] = false,
            ["Notice"] = DiffBodyNotice,
            ["InitialDiscussionIncluded"] = false,
        };
        files.Add(
            new(
                prefix + "setup.md",
                "# Review setup\n\n" + WorkflowMarkdown.YamlDocument(setup) + BindingTrailer(binding)
            )
        );
        var publication =
            canonical["Publication"] as JsonObject ?? throw new InvalidDataException("Publication receipt is missing.");
        if (
            publication["Outcome"]?.GetValue<string>() != "no_op"
            || publication["Actions"] is not JsonArray { Count: 0 }
        )
            throw new InvalidDataException("Markdown retention must not claim source publication.");
        files.Add(
            Json(prefix + "publication.json", binding, Redact(prefix + "publication.json", publication).AsObject())
        );
        files.Add(
            Json(
                prefix + "identities.json",
                binding,
                new JsonObject
                {
                    ["Provider"] = repo.Provider,
                    ["RepoKey"] = repo.NormalizedKey,
                    ["PrId"] = run.PrId,
                    ["ReviewRunId"] = run.Id,
                    ["Mode"] = run.Mode,
                    ["ArtifactBranch"] = binding.ToJson()["ArtifactBranch"]?.DeepClone(),
                }
            )
        );
        files.Add(Json(prefix + "receipts.json", binding, inventory.DeepClone().AsObject()));
        foreach (var file in files)
            ValidateNoSecrets(file);
        return files;
    }

    /// <summary>Assembles one exported JSON file: fixed schema version, the run binding, then the body.</summary>
    private static ReviewArtifactFile Json(string path, ReviewArtifactExportBinding binding, JsonObject body)
    {
        var payload = new JsonObject { ["SchemaVersion"] = SchemaVersion, ["Binding"] = binding.ToJson() };
        foreach (var property in body.ToList())
        {
            _ = body.Remove(property.Key);
            payload[property.Key] = property.Value;
        }
        return new ReviewArtifactFile(path, payload.ToJsonString());
    }

    /// <summary>The fixed marker that opens <c>review.md</c>'s binding trailer.</summary>
    internal const string BindingMarkerPrefix = "<!-- review-artifact-binding: ";

    /// <summary>The fixed marker that closes it.</summary>
    internal const string BindingMarkerSuffix = " -->";

    /// <summary>
    /// The machine-readable binding for a prose file: the same canonical binding string every JSON file
    /// carries, wrapped in an HTML comment so it renders as nothing and parses deterministically.
    /// <para>
    /// It is a TRAILER, and validation requires the content to END with it. That is what makes a forged
    /// trailer inside the review text harmless: a model can write anything it likes into the body, but the
    /// exporter appends the real one afterwards, so the last one is always the one that was checked.
    /// </para>
    /// </summary>
    internal static string BindingTrailer(ReviewArtifactExportBinding binding) =>
        "\n\n" + BindingMarkerPrefix + binding.ToCanonicalString() + BindingMarkerSuffix + "\n";

    /// <summary>
    /// Redacts every string inside an untrusted JSON subtree, returning a redacted CLONE. The original node
    /// is left untouched, which is what keeps the private artifact and the published copy different
    /// documents rather than one document the export mutated on its way past.
    /// </summary>
    private static T Redact<T>(string path, T node)
        where T : JsonNode
    {
        var clone = (T)node.DeepClone();
        RedactInPlace(path, clone);
        return clone;
    }

    private static void RedactInPlace(string path, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToList())
                {
                    if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        jsonObject[property.Key] = Redact(path, text);
                    }
                    else
                    {
                        RedactInPlace(path, property.Value);
                    }
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[index] = Redact(path, text);
                    }
                    else
                    {
                        RedactInPlace(path, array[index]);
                    }
                }
                break;
            default:
                break;
        }
    }

    private static string Redact(string path, string text) => Redact(path, text, CredentialPatterns.RedactNamed);

    /// <summary>
    /// The redaction of one untrusted string, with the sweep as a parameter so the timeout arm is reachable
    /// from a test. Production callers use the two-argument overload.
    /// </summary>
    /// <remarks>
    /// Redaction is fail-closed for exactly the same reason the scan is: text that was only PARTIALLY
    /// redacted looks identical to text that had nothing to redact. Returning the input here — or the
    /// partially-rewritten string — would publish the bytes we could not finish inspecting.
    /// </remarks>
    /// <param name="path">The artifact path, used only to name the file in a refusal.</param>
    /// <param name="text">The untrusted text.</param>
    /// <param name="redactNamed">The sweep: text in, redacted text out.</param>
    internal static string Redact(string path, string text, Func<string, string> redactNamed)
    {
        ArgumentNullException.ThrowIfNull(redactNamed);
        try
        {
            return redactNamed(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Same reasoning as the scan below: never keep the original as an InnerException, because
            // RegexMatchTimeoutException carries the text it was matching in its Input property.
            throw new InvalidOperationException($"'{path}' {ScanTimedOutDetail}");
        }
    }

    /// <summary>
    /// Refuses a file whose content carries a credential shape, naming the SHAPE and never the match.
    /// <para>
    /// Fail-closed in both directions: a recognized shape refuses, and so does a scan that could not finish
    /// (a match timeout on pathological input). "I could not inspect this" is not "this is clean", and a
    /// pushed branch cannot be unpublished — there is no later opportunity to reconsider.
    /// </para>
    /// <para>
    /// The false positives are real and accepted. Review prose legitimately discusses
    /// <c>api_key = …</c>-shaped code, and the labelled-assignment pattern will refuse a bundle over it.
    /// That is the correct trade for a one-way publish: the failure mode is an operator investigating a
    /// refusal, against a failure mode of a credential on a git branch forever.
    /// </para>
    /// </summary>
    public static void ValidateNoSecrets(ReviewArtifactFile file) =>
        ValidateNoSecrets(file, CredentialPatterns.FindFirstName);

    /// <summary>
    /// The refusal logic, with the scan as a parameter so the timeout arm is reachable from a test.
    /// Production callers use <see cref="ValidateNoSecrets(ReviewArtifactFile)"/>.
    /// </summary>
    /// <remarks>
    /// The arm exists to be exercised. A fail-closed claim that no test drives is indistinguishable from a
    /// fail-open one, and inverting the <c>catch</c> below to <c>return</c> is a silent change from
    /// "withhold what I could not inspect" to "publish it unexamined".
    /// </remarks>
    /// <param name="file">The file to inspect.</param>
    /// <param name="findFirstName">The scan: content in, matched shape name or <c>null</c> out.</param>
    internal static void ValidateNoSecrets(ReviewArtifactFile file, Func<string, string?> findFirstName)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(findFirstName);
        string? shape;
        try
        {
            shape = findFirstName(file.Content);
        }
        catch (RegexMatchTimeoutException)
        {
            // Deliberately NOT `throw new InvalidOperationException(message, exception)`.
            // RegexMatchTimeoutException carries the text it was matching against in its Input property,
            // so keeping it as an InnerException would write the UNSCANNED content — the exact bytes this
            // method could not prove clean — into every log line, crash dump and error report that renders
            // the exception chain. A fail-closed scanner that leaks what it refused to publish has simply
            // moved the leak somewhere with worse retention. Nothing derived from the content survives
            // this frame: the message below is a constant and the path is built from the branch and
            // instance id.
            throw new InvalidOperationException($"'{file.RelativePath}' {ScanTimedOutDetail}");
        }
        if (shape is not null)
        {
            throw new InvalidOperationException(
                $"'{file.RelativePath}' matches the '{shape}' credential shape and will not be retained."
            );
        }
    }

    /// <summary>The constant half of the timeout refusal — no content, no shape, no matched text.</summary>
    internal const string ScanTimedOutDetail =
        "could not be scanned for credentials within the match timeout and will not be retained. The "
        + "content is deliberately omitted from this message: it is the text that could not be proven "
        + "free of credentials.";

    /// <summary>
    /// Validates one exported file on the way back IN — the bundle on disk is replayed on a resume, so the
    /// export has to be checked when it is read as well as when it is built.
    /// <para>
    /// The binding check is the point. Re-scanning for secrets catches a file that was tampered with to ADD
    /// one; comparing the binding catches a file that is entirely valid and entirely someone else's — a
    /// bundle whose <c>findings.json</c> was lifted from another run, another PR or another branch. Both
    /// are cheap and neither subsumes the other.
    /// </para>
    /// </summary>
    /// <param name="file">The file as read back from the cached bundle.</param>
    /// <param name="fileName">Its bare name, already matched against <see cref="IsExportFileName"/>.</param>
    /// <param name="binding">The identity the current scope says this file must describe.</param>
    public static void ValidateExportedArtifact(
        ReviewArtifactFile file,
        string fileName,
        ReviewArtifactExportBinding binding
    )
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateNoSecrets(file);
        if (fileName is "review.md" or "grade.md" or "comments.md" or "performance.md" or "discussion.md" or "setup.md")
        {
            // Prose carries its binding as a trailer. Requiring the content to END with it is what makes a
            // forged trailer inside the review text harmless: the exporter appends the real one last, so
            // the only trailer this can match is the one the exporter wrote.
            if (!file.Content.EndsWith(BindingTrailer(binding), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"'{file.RelativePath}' is bound to a different review run, pull request or branch than "
                        + "the one being retained."
                );
            }
            return;
        }
        if (
            JsonNode.Parse(file.Content) is not JsonObject payload
            || payload["SchemaVersion"]?.GetValue<int>() != SchemaVersion
        )
        {
            throw new InvalidOperationException($"'{file.RelativePath}' is not a version {SchemaVersion} export.");
        }
        if (!string.Equals(payload["Binding"]?.ToJsonString(), binding.ToCanonicalString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{file.RelativePath}' is bound to a different review run, pull request or branch than the "
                    + "one being retained."
            );
        }
    }

    /// <summary>The per-instance directory the summary and the export share.</summary>
    public static string BuildPrefix(string artifactBranch, string instanceDirectoryName) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"PRs/{artifactBranch["review/".Length..]}/{instanceDirectoryName}/"
        );
}
