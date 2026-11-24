using System.Text.Json.Nodes;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
///     A study store in research-store.mjs's format: <c>study.json</c> and one <c>records/&lt;id&gt;.json</c> per
///     record, under <c>.experimentation/reviewer-evolution/</c> of <c>store</c>.
/// </summary>
internal static class StudyFixture
{
    internal static void Write(string store, params (string Id, string Kind, string? Supersedes)[] records)
    {
        var root = Path.Combine(store, ".experimentation", "reviewer-evolution");
        Directory.CreateDirectory(Path.Combine(root, "records"));
        File.WriteAllText(
            Path.Combine(root, "study.json"),
            """{"schemaVersion":1,"id":"reviewer-evolution","question":"q","createdAt":"2026-11-23T00:00:00.000Z"}"""
        );
        foreach (var (id, kind, supersedes) in records)
        {
            var record = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["id"] = id,
                ["kind"] = kind,
                ["title"] = id,
                ["summary"] = id,
                ["createdAt"] = "2026-11-23T00:00:00.000Z",
                ["links"] = new JsonArray(),
                ["data"] = new JsonObject(),
            };
            if (supersedes is not null)
            {
                record["supersedes"] = supersedes;
            }
            File.WriteAllText(Path.Combine(root, "records", id + ".json"), record.ToJsonString());
        }
    }
}
