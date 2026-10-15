extern alias Reviewd;

using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewdCapacityTests
{
    [Fact]
    public async Task Preview_holds_uncertified_owners_and_assignments_without_mutating_the_database()
    {
        var database = Path.Combine(Path.GetTempPath(), $"reviewd-capacity-{Guid.NewGuid():N}.db");
        const string repository = "org/project/Nova";
        var repo = PrCommands.ParseRepo(repository);
        var ownerKey = $"reviewd/review-attempt/{repo.NormalizedKey}/17";
        var receiptKey = $"reviewd/review-run/{repo.NormalizedKey}/17";
        try
        {
            using (var connection = PrThreadBindings.Connection(database))
                _ = new SqliteKvStore(connection);
            var evidence = new JsonObject
            {
                ["workspaceId"] = "workspace",
                ["sessionId"] = "new-session",
                ["operationId"] = "native-inspection",
                ["sharedQuiescent"] = true,
                ["producerQuiescent"] = true,
                ["slots"] = new JsonArray(
                    Enumerable
                        .Range(0, 6)
                        .Select(slot =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["repository"] = repository,
                                    ["slot"] = slot,
                                    ["nativeQuiescent"] = true,
                                    ["artifactsRetained"] = true,
                                    ["quiescentThreads"] = new JsonArray(),
                                }
                        )
                        .ToArray()
                ),
            };
            var missing = await PreviewAsync([]);
            missing["lanes"]![repository]!.AsArray().Should().BeEmpty();
            missing["slots"]!.AsArray().Should().OnlyContain(slot => slot!["status"]!.GetValue<string>() == "held");
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 1, 2, 3, 4, 5);

            var owner = new JsonObject
            {
                ["workspaceId"] = "workspace",
                ["repository"] = repository,
                ["slot"] = 2,
                ["prId"] = "17",
                ["sessionId"] = "old-session",
                ["stage"] = "prepare-pending",
            };
            await PutAsync(ownerKey, owner);
            await PutAsync(
                "reviewd/batch-state/historical",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["workspace"] = "workspace" },
                    ["entries"] = new JsonArray(
                        new JsonObject
                        {
                            ["repo"] = repository,
                            ["slot"] = 1,
                            ["pr"] = "99",
                            ["status"] = "queued",
                        }
                    ),
                }
            );
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 3, 4, 5);

            var proof = owner.DeepClone().AsObject();
            proof["schemaVersion"] = 1;
            proof["attemptDigest"] = SlotRelease.Digest(owner.ToJsonString());
            proof["classification"] = "preparation-settled-before-review";
            proof["noAdmission"] = true;
            proof["threadQuiescent"] = true;
            proof["nativeQuiescent"] = true;
            proof["evidence"] = new JsonArray(new JsonObject { ["path"] = "supplied-native-proof" });
            await PutAsync(SlotRelease.Key(owner.ToJsonString()), proof);
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 2, 3, 4, 5);
            var sameSession = evidence.DeepClone().AsObject();
            sameSession["sessionId"] = "old-session";
            Candidates(await PreviewAsync(sameSession, "old-session")).Should().Equal(0, 3, 4, 5);
            owner["stage"] = "prepare-completed";
            await PutAsync(ownerKey, owner);
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 3, 4, 5);

            owner["stage"] = "completed-verified";
            owner["threadId"] = "owner-thread";
            owner["headSha"] = new string('a', 40);
            owner["artifactCommit"] = new JsonObject { ["status"] = "committed" };
            var receipt = owner.DeepClone().AsObject();
            receipt["reviewedHeadSha"] = new string('a', 40);
            await PutAsync(ownerKey, owner);
            await PutAsync(receiptKey, receipt);
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 3, 4, 5);
            evidence["slots"]![2]!["quiescentThreads"] = new JsonArray("owner-thread");
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 3, 4, 5);
            owner["cleanupState"] = "released";
            owner["claimToken"] = "cleanup-claim";
            await PutAsync(ownerKey, owner);
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 2, 3, 4, 5);
            await PutAsync(
                SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 2),
                new JsonObject { ["claimToken"] = "new-owner", ["state"] = "owned" }
            );
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 3, 4, 5);
            await PutAsync(
                SlotRelease.OwnerKey("workspace", repo.NormalizedKey, 2),
                new JsonObject { ["claimToken"] = "new-owner", ["state"] = "released" }
            );
            foreach (var status in new[] { "closed", "stale", "closed-held" })
            {
                await PutAsync(
                    "reviewd/batch-state/historical",
                    new JsonObject
                    {
                        ["scope"] = new JsonObject { ["workspace"] = "workspace" },
                        ["entries"] = new JsonArray(
                            new JsonObject
                            {
                                ["repo"] = repository,
                                ["slot"] = 1,
                                ["pr"] = "99",
                                ["status"] = status,
                            }
                        ),
                    }
                );
                Candidates(await PreviewAsync(evidence))
                    .Should()
                    .Equal(status == "closed-held" ? [0, 2, 3, 4, 5] : [0, 1, 2, 3, 4, 5]);
            }

            owner["stage"] = "review-completed";
            owner["reviewInputId"] = "retained-original-input";
            await PutAsync(ownerKey, owner);
            using (var connection = PrThreadBindings.Connection(database))
            {
                connection.Open();
                using var removePriorScenario = connection.CreateCommand();
                removePriorScenario.CommandText = "DELETE FROM cache WHERE key=@key";
                removePriorScenario.Parameters.AddWithValue("@key", receiptKey);
                removePriorScenario.ExecuteNonQuery();
            }
            var retirement = owner.DeepClone().AsObject();
            retirement["schemaVersion"] = 1;
            retirement["attemptDigest"] = SlotRelease.Digest(owner.ToJsonString());
            retirement["classification"] = "operator-retired-after-reset";
            retirement["recoverySessionId"] = "new-session";
            retirement["writersRetired"] = true;
            retirement["producersStopped"] = true;
            retirement["threadQuiescent"] = true;
            retirement["nativeQuiescent"] = true;
            retirement["resetReceiptPath"] = "reset.json";
            retirement["archiveManifestPath"] = "archive.json";
            retirement["evidence"] = new JsonArray(new JsonObject());
            await PutAsync(
                "reviewd/batch-state/historical",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["workspace"] = "workspace" },
                    ["entries"] = new JsonArray(
                        new JsonObject
                        {
                            ["repo"] = repository,
                            ["slot"] = 2,
                            ["pr"] = "17",
                            ["status"] = "retired",
                        }
                    ),
                }
            );
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 1, 3, 4, 5);
            await PutAsync(SlotRelease.Key(owner.ToJsonString()), retirement);
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 1, 2, 3, 4, 5);
            retirement["attemptDigest"] = "wrong";
            await PutAsync(SlotRelease.Key(owner.ToJsonString()), retirement);
            Candidates(await PreviewAsync(evidence)).Should().Equal(0, 1, 3, 4, 5);

            async Task PutAsync(string key, JsonObject value)
            {
                using var connection = PrThreadBindings.Connection(database);
                await new SqliteKvStore(connection).SetAsync(key, value);
            }

            async Task<JsonObject> PreviewAsync(JsonObject supplied, string session = "new-session")
            {
                // Pooled connections keep the file open; on Windows File.ReadAllBytes cannot open it then.
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var before = await File.ReadAllBytesAsync(database);
                var report = await ReviewCapacity.PreviewAsync(
                    database,
                    "workspace",
                    session,
                    [repository],
                    supplied,
                    CancellationToken.None
                );
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                (await File.ReadAllBytesAsync(database)).Should().Equal(before);
                report["previewOnly"]!.GetValue<bool>().Should().BeTrue();
                report["boundary"]!
                    .GetValue<string>()
                    .Should()
                    .Contain("Evidence is supplied")
                    .And.Contain("No reservation, reset or reassignment performed");
                return report;
            }

            static IEnumerable<int> Candidates(JsonObject report) =>
                report["lanes"]![repository]!.AsArray().Select(slot => slot!.GetValue<int>());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }
}
