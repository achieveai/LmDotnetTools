using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Sluicehook.Models;

public sealed record AllocateConsignmentRequest(
    string Id,
    IReadOnlyList<AllocateConsignmentLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record AllocateConsignmentLine(string Sku, int Quantity, string? Note);

public sealed record StageCrateRequest(string Id, IReadOnlyList<StageCrateLine> Lines, DateTimeOffset ReceivedAt);

public sealed record StageCrateLine(string Sku, int Quantity, string? Note);

public sealed record QuoteBatchRequest(string Id, IReadOnlyList<QuoteBatchLine> Lines, DateTimeOffset ReceivedAt);

public sealed record QuoteBatchLine(string Sku, int Quantity, string? Note);
