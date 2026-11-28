using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Sandbox;

// A class, not a record, by exception to .github/instructions/csharp-code-rules.instructions.md "Data objects": the
// Sandbox request and result types are sealed classes (SandboxCreateRequest, SandboxInfo, SandboxArchivePreparation),
// and a record's generated ToString would print RawSourceReceipt, whose JSON can name private repository URLs
// (SandboxSourceReceiptEntry redacts its own).
/// <summary>
/// What <see cref="SandboxClient.PrepareSourcesAsync"/> or <see cref="SandboxClient.ResumeSourcePreparationAsync"/>
/// captured. A failed preparation is never returned: it throws <see cref="SandboxException"/>, with
/// <see cref="SandboxException.ErrorCode"/> set when the gateway supplied one (a failed body or error response without
/// an <c>error_code</c> leaves it <see langword="null"/>).
/// </summary>
public sealed class SandboxSourcePreparation
{
    public required SandboxSourcePreparationStatus Status { get; init; }

    /// <summary>The last operation id the gateway named, or <see langword="null"/> when it answered at once.</summary>
    public string? OperationId { get; init; }

    /// <summary>The version to send as <see cref="SandboxCreateRequest.ExpectedSourceVersion"/>; null when unversioned.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>The gateway's <c>error_code</c> on a non-failed reply (<c>source_snapshot_unsupported</c> when unversioned).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Each captured catalog or plugin root, with the commit it resolved to. Always set when <see cref="Status"/> is
    /// <see cref="SandboxSourcePreparationStatus.Ready"/>. <see langword="null"/> only for an
    /// <see cref="SandboxSourcePreparationStatus.Unversioned"/> reply without a receipt, which a gateway running without
    /// a marketplace store sends.
    /// </summary>
    public IReadOnlyList<SandboxSourceReceiptEntry>? Sources { get; init; }

    /// <summary>
    /// The whole <c>sourceReceipt</c> object as the gateway sent it, for fields this SDK does not model;
    /// <see langword="null"/> exactly when <see cref="Sources"/> is.
    /// </summary>
    public JsonElement? RawSourceReceipt { get; init; }
}
