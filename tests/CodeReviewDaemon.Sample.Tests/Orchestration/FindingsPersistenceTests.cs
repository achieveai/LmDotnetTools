using System.Text.Json;
using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>Compatibility readback of historical findings and nullable match evidence.</summary>
public sealed class FindingsPersistenceTests
{
    /// <summary>
    /// F-001 — a schema-v1 artifact written before match tracing existed has no <c>MatchScore</c> or
    /// <c>MatchTiedCandidates</c> property in its JSON at all. Deserialised into today's
    /// <see cref="ReviewFindingRecord"/>, those fields must come back <see langword="null"/> — an EXPLICIT
    /// "not measured" — rather than <c>0</c>, which already means "measured, no candidate matched" on a
    /// freshly built dropped row. A non-nullable field would have hydrated both cases identically.
    /// </summary>
    [Fact]
    public void A_legacy_pre_match_tracing_payload_hydrates_match_fields_as_null_not_zero()
    {
        // No `MatchScore`/`MatchTiedCandidates`/`ShippedTitle` properties at all — exactly what
        // JsonSerializer.Serialize produced before this field pair existed.
        const string legacyJson = """
            {
              "Round": 1,
              "CapturedAtUtc": "2026-01-01T00:00:00Z",
              "PromptTemplateHash": null,
              "Compared": true,
              "ParsedCount": 2,
              "RecordedCount": 2,
              "Sources": [],
              "Findings": [
                {
                  "Source": "architecture",
                  "Template": "reviewer",
                  "Title": "[BLOCKER] High — DI coupling",
                  "Location": "src/Foo.cs:1",
                  "Severity": "Blocker/High",
                  "SeverityTokens": ["Blocker"],
                  "Outcome": "kept",
                  "ShippedSeverity": "Blocker/High",
                  "ShippedTitle": "[BLOCKER] High — DI coupling",
                  "SynthesisNote": null
                },
                {
                  "Source": "architecture",
                  "Template": "reviewer",
                  "Title": "[MEDIUM] unchecked cast",
                  "Location": "src/Bar.cs:1",
                  "Severity": "Medium",
                  "SeverityTokens": ["Medium"],
                  "Outcome": "dropped",
                  "ShippedSeverity": null,
                  "ShippedTitle": null,
                  "SynthesisNote": null
                }
              ]
            }
            """;

        var payload = JsonSerializer.Deserialize<ReviewFindingsArtifactPayload>(legacyJson);

        payload.Should().NotBeNull();
        foreach (var row in payload!.Findings)
        {
            row.MatchScore.Should().BeNull("this row predates match tracing and was never scored");
            row.MatchTiedCandidates.Should().BeNull("this row predates match tracing and was never scored");
        }

        // The whole point: a legacy `kept` row and a legacy `dropped` row must be equally distinguishable
        // from a freshly measured zero. Neither reads as "scored 0" — see the non-legacy assertion below.
        payload.AmbiguousMatches.Should().Be(0, "a null MatchTiedCandidates must not count as ambiguous");

        // Same JSON also predates ShippedIndex — no such property either. It must hydrate null, and
        // critically must NOT collapse to -1 (the real, MEASURED "dropped" sentinel a fresh row would
        // carry), which would misreport a never-tracked legacy row as a deliberately dropped one.
        foreach (var row in payload.Findings)
        {
            row.ShippedIndex.Should().BeNull("this row predates ShippedIndex and its identity was never tracked");
        }
    }

    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 3, 1)]
    public void Historical_match_evidence_round_trips_without_reinterpreting_titles(
        int score,
        int ties,
        int shippedIndex
    )
    {
        var row = new ReviewFindingRecord(
            "reviewer",
            "template",
            "same title",
            "src/Foo.cs:10",
            "Low",
            ["Low"],
            "kept",
            "Low",
            "same title",
            null,
            score,
            ties,
            shippedIndex
        );
        var payload = new ReviewFindingsArtifactPayload(1, "2026-01-01T00:00:00Z", null, true, 1, 1, [], [row]);
        var restored = JsonSerializer.Deserialize<ReviewFindingsArtifactPayload>(JsonSerializer.Serialize(payload))!;
        var actual = restored.Findings.Should().ContainSingle().Subject;
        actual.MatchScore.Should().Be(score);
        actual.MatchTiedCandidates.Should().Be(ties);
        actual.ShippedIndex.Should().Be(shippedIndex);
        restored.AmbiguousMatches.Should().Be(ties > 1 ? 1 : 0);
    }
}
