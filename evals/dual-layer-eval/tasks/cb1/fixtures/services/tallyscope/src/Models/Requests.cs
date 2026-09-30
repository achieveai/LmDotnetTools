using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Tallyscope.Models;

public sealed record DispatchConsignmentRequest(
    string Id,
    IReadOnlyList<DispatchConsignmentLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record DispatchConsignmentLine(string Sku, int Quantity, string? Note);

public sealed record MergeInvoiceRequest(string Id, IReadOnlyList<MergeInvoiceLine> Lines, DateTimeOffset ReceivedAt);

public sealed record MergeInvoiceLine(string Sku, int Quantity, string? Note);

public sealed record SettleVoyageRequest(string Id, IReadOnlyList<SettleVoyageLine> Lines, DateTimeOffset ReceivedAt);

public sealed record SettleVoyageLine(string Sku, int Quantity, string? Note);
