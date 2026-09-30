using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Ledgerbridge.Models;

public sealed record ReconcileBookingRequest(
    string Id,
    IReadOnlyList<ReconcileBookingLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ReconcileBookingLine(string Sku, int Quantity, string? Note);

public sealed record DispatchSlotRequest(string Id, IReadOnlyList<DispatchSlotLine> Lines, DateTimeOffset ReceivedAt);

public sealed record DispatchSlotLine(string Sku, int Quantity, string? Note);

public sealed record QuoteCrateRequest(string Id, IReadOnlyList<QuoteCrateLine> Lines, DateTimeOffset ReceivedAt);

public sealed record QuoteCrateLine(string Sku, int Quantity, string? Note);
