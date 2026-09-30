using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Skiffrelay.Models;

public sealed record DispatchDocketRequest(
    string Id,
    IReadOnlyList<DispatchDocketLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record DispatchDocketLine(string Sku, int Quantity, string? Note);

public sealed record AuditPalletRequest(string Id, IReadOnlyList<AuditPalletLine> Lines, DateTimeOffset ReceivedAt);

public sealed record AuditPalletLine(string Sku, int Quantity, string? Note);

public sealed record ArchiveInvoiceRequest(
    string Id,
    IReadOnlyList<ArchiveInvoiceLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ArchiveInvoiceLine(string Sku, int Quantity, string? Note);
