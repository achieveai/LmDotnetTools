using System.Collections.Immutable;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>Durable opt-in binding and report, read together with the original run ledger.</summary>
public sealed record FinalReportProof(
    string InputId,
    DateTimeOffset DeadlineUtc,
    string? RunId = null,
    string? Report = null
)
{
    private const string PropertyPrefix = "final_report:";

    public static FinalReportProof? Read(ThreadMetadata? metadata, string inputId)
    {
        if (metadata?.Properties?.TryGetValue(PropertyPrefix + inputId, out var value) != true)
        {
            return null;
        }
        return value is FinalReportProof proof
            ? proof
            : JsonSerializer.Deserialize<FinalReportProof>(JsonSerializer.Serialize(value));
    }

    public static Task BindAsync(
        IConversationStore store,
        string threadId,
        string inputId,
        DateTimeOffset deadlineUtc,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.UpdateMetadataAsync(
            threadId,
            metadata =>
            {
                if (metadata is null)
                {
                    throw new InvalidOperationException(
                        "A final-report input requires persisted conversation metadata."
                    );
                }
                var existing = Read(metadata, inputId);
                if (existing is not null && existing.DeadlineUtc != deadlineUtc)
                {
                    throw new InvalidOperationException("The final-report deadline differs from its original binding.");
                }
                return existing is not null
                    ? metadata
                    : metadata with
                    {
                        Properties = (metadata.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
                            PropertyPrefix + inputId,
                            new FinalReportProof(inputId, deadlineUtc)
                        ),
                    };
            },
            ct
        );
    }

    public static Task SaveAsync(
        IConversationStore store,
        string threadId,
        FinalReportProof proof,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(proof);
        return store.UpdateMetadataAsync(
            threadId,
            metadata =>
            {
                var binding = Read(metadata, proof.InputId);
                if (
                    metadata is null
                    || binding is null
                    || binding.DeadlineUtc != proof.DeadlineUtc
                    || string.IsNullOrWhiteSpace(proof.RunId)
                    || string.IsNullOrWhiteSpace(proof.Report)
                    || proof.Report.Length > 1_048_576
                    || (binding.RunId is not null && binding != proof)
                )
                {
                    throw new InvalidOperationException("The final report does not match its persisted binding.");
                }
                return metadata with
                {
                    Properties = metadata.Properties!.SetItem(PropertyPrefix + proof.InputId, proof),
                };
            },
            ct
        );
    }
}
