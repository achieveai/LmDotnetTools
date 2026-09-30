using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Parcelrelay.Models;

public sealed record StageConsignmentRequest(
    string Id,
    IReadOnlyList<StageConsignmentLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record StageConsignmentLine(string Sku, int Quantity, string? Note);

public sealed record ValidateReceiptRequest(
    string Id,
    IReadOnlyList<ValidateReceiptLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ValidateReceiptLine(string Sku, int Quantity, string? Note);

public sealed record MergeSlotRequest(string Id, IReadOnlyList<MergeSlotLine> Lines, DateTimeOffset ReceivedAt);

public sealed record MergeSlotLine(string Sku, int Quantity, string? Note);
