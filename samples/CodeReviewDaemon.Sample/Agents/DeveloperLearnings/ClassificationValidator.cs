using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

/// <summary>Prose the model wrote for a brand-new pattern. The only free text it is ever allowed to emit.</summary>
internal sealed record ProposedPattern(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("whatItIs")] string WhatItIs,
    [property: JsonPropertyName("whyItMatters")] string WhyItMatters,
    [property: JsonPropertyName("howToAvoid")] string HowToAvoid);

/// <summary>One finding's classification. Exactly one of the two pattern routes is populated.</summary>
internal sealed record FindingClassification(
    [property: JsonPropertyName("findingRef")] string FindingRef,
    [property: JsonPropertyName("isRecurringRisk")] bool IsRecurringRisk,
    [property: JsonPropertyName("patternId")] string? PatternId,
    [property: JsonPropertyName("newPattern")] ProposedPattern? NewPattern);

/// <summary>The model's whole reply.</summary>
internal sealed record ClassificationReply(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("classifications")] IReadOnlyList<FindingClassification> Classifications);

/// <summary>Accepted classifications, or the reason the whole reply was refused.</summary>
internal sealed record ClassificationOutcome(
    IReadOnlyList<FindingClassification> Accepted,
    string? RejectionReason)
{
    public bool Rejected => RejectionReason is not null;
}

/// <summary>
/// The boundary where "the model classifies, the daemon counts" stops being a sentence in a prompt and
/// becomes a property of the system.
/// <para>
/// <b>Everything here is enforced in code, deliberately, and none of it is delegated to prompt wording.</b>
/// A prompt instruction is a request; a validator is a guarantee. Prompt-delivered capability goes unused
/// while code-wired capability works, and the same asymmetry applies to prohibitions.
/// </para>
/// <para>
/// <b>Rejection is whole-reply, not per-item.</b> A model that emitted a count where none was permitted has
/// misunderstood its contract, and the classifications it produced in the same breath are not more
/// trustworthy for being syntactically valid. Salvaging the parts that happen to parse is how a
/// misunderstood contract becomes a permanent silent corruption of the ledger.
/// </para>
/// </summary>
internal static partial class ClassificationValidator
{
    /// <summary>
    /// The only shape a pattern slug may take. This is a PATH SEGMENT — it becomes
    /// <c>patterns/{slug}.md</c> — so the character class is a security boundary, not a naming convention.
    /// Anchored, lower-case, no dots and no slashes, which makes <c>../../.git/hooks/x</c> unconstructible
    /// rather than filtered. Filtering traversal is a blocklist; this is an allowlist.
    /// </summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,63}$")]
    private static partial Regex SlugShape();

    /// <summary>
    /// Text that means the model tried to author a fact the daemon owns. Any digit run of two or more, an
    /// ISO-ish date, or a state word are all disqualifying inside the three description fields.
    /// <para>
    /// Deliberately blunt. A false positive costs one rejected reply and a retry; a false negative puts a
    /// model-invented count under a named person's file, where it is indistinguishable from a measured one
    /// and will be read as fact. Single digits survive because "a null check" style prose legitimately
    /// contains them and they cannot express a count or a date on their own.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"\d{2,}"
            + @"|\b\d{4}-\d{2}-\d{2}\b"
            + @"|\b(?:active|resolved|watch|unjudgeable|regressed|provisional)\b"
            + @"|\boccurrence(?:s)?\b"
            + @"|\bclean\s+streak\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DaemonOwnedFact();

    /// <summary>Longest description field. Prose, not a document.</summary>
    private const int MaxDescriptionChars = 1200;

    /// <summary>
    /// Parses and validates one reply against the supplied finding refs and the developer's existing
    /// pattern ids.
    /// </summary>
    /// <param name="json">The model's raw reply.</param>
    /// <param name="knownFindingRefs">Opaque ids the daemon handed out. Anything else is not about this PR.</param>
    /// <param name="knownPatternIds">The developer's existing patterns, by id.</param>
    public static ClassificationOutcome Validate(
        string? json,
        IReadOnlySet<string> knownFindingRefs,
        IReadOnlySet<string> knownPatternIds)
    {
        ArgumentNullException.ThrowIfNull(knownFindingRefs);
        ArgumentNullException.ThrowIfNull(knownPatternIds);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new ClassificationOutcome([], "the reply was empty");
        }

        ClassificationReply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<ClassificationReply>(json);
        }
        catch (JsonException ex)
        {
            return new ClassificationOutcome([], $"the reply was not valid JSON: {ex.Message}");
        }

        if (reply?.Classifications is null)
        {
            return new ClassificationOutcome([], "the reply carried no classifications array");
        }

        if (reply.SchemaVersion != DeveloperObservation.CurrentSchemaVersion)
        {
            return new ClassificationOutcome(
                [], $"schemaVersion {reply.SchemaVersion} is not the expected {DeveloperObservation.CurrentSchemaVersion}");
        }

        var accepted = new List<FindingClassification>();
        var claimedSlugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in reply.Classifications)
        {
            if (item.FindingRef is null || !knownFindingRefs.Contains(item.FindingRef))
            {
                return new ClassificationOutcome(
                    [], $"findingRef '{item.FindingRef ?? "(null)"}' was not one this PR handed out");
            }

            // Not every finding is a learning. A one-off or environmental finding must be droppable, or the
            // ledger fills with noise that dilutes every rate in it.
            if (!item.IsRecurringRisk)
            {
                if (item.PatternId is not null || item.NewPattern is not null)
                {
                    return new ClassificationOutcome(
                        [], $"findingRef '{item.FindingRef}' is not a recurring risk yet named a pattern");
                }

                accepted.Add(item);
                continue;
            }

            var hasId = item.PatternId is not null;
            var hasNew = item.NewPattern is not null;
            if (hasId == hasNew)
            {
                return new ClassificationOutcome(
                    [], $"findingRef '{item.FindingRef}' must set exactly one of patternId / newPattern");
            }

            if (hasId)
            {
                // An unrecognised id is REJECTED, never auto-created. Silent creation from a mistyped id is
                // precisely how one mistake becomes three pattern files whose counts never accumulate.
                if (!knownPatternIds.Contains(item.PatternId!))
                {
                    return new ClassificationOutcome(
                        [], $"patternId '{item.PatternId}' is not one of this developer's known patterns");
                }

                accepted.Add(item);
                continue;
            }

            var proposed = item.NewPattern!;
            if (proposed.Slug is null || !SlugShape().IsMatch(proposed.Slug))
            {
                return new ClassificationOutcome(
                    [], $"proposed slug '{proposed.Slug ?? "(null)"}' is not a legal pattern slug");
            }

            if (knownPatternIds.Contains(proposed.Slug) || !claimedSlugs.Add(proposed.Slug))
            {
                return new ClassificationOutcome(
                    [], $"proposed slug '{proposed.Slug}' collides with a pattern that already exists");
            }

            var offending = FirstDaemonOwnedFact(proposed);
            if (offending is not null)
            {
                return new ClassificationOutcome(
                    [], $"proposed pattern '{proposed.Slug}' stated a fact the daemon owns: {offending}");
            }

            accepted.Add(item);
        }

        return new ClassificationOutcome(accepted, RejectionReason: null);
    }

    /// <summary>The first description field that overstepped, described, or null when all three are clean.</summary>
    private static string? FirstDaemonOwnedFact(ProposedPattern proposed)
    {
        foreach (var (name, text) in new[]
        {
            ("title", proposed.Title),
            ("whatItIs", proposed.WhatItIs),
            ("whyItMatters", proposed.WhyItMatters),
            ("howToAvoid", proposed.HowToAvoid),
        })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return $"{name} was empty";
            }

            if (text.Length > MaxDescriptionChars)
            {
                return $"{name} was {text.Length} chars, over the {MaxDescriptionChars} limit";
            }

            var match = DaemonOwnedFact().Match(text);
            if (match.Success)
            {
                return $"{name} contains '{match.Value}'";
            }
        }

        return null;
    }
}
