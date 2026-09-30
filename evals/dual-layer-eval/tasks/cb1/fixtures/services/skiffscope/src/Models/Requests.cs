using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Skiffscope.Models;

public sealed record MergeHoldRequest(string Id, IReadOnlyList<MergeHoldLine> Lines, DateTimeOffset ReceivedAt);

public sealed record MergeHoldLine(string Sku, int Quantity, string? Note);

public sealed record ReconcileBatchRequest(
    string Id,
    IReadOnlyList<ReconcileBatchLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ReconcileBatchLine(string Sku, int Quantity, string? Note);

public sealed record PriceReceiptRequest(string Id, IReadOnlyList<PriceReceiptLine> Lines, DateTimeOffset ReceivedAt);

public sealed record PriceReceiptLine(string Sku, int Quantity, string? Note);
