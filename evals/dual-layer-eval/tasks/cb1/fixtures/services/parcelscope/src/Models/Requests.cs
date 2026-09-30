using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Parcelscope.Models;

public sealed record PriceBookingRequest(string Id, IReadOnlyList<PriceBookingLine> Lines, DateTimeOffset ReceivedAt);

public sealed record PriceBookingLine(string Sku, int Quantity, string? Note);

public sealed record ReplayHoldRequest(string Id, IReadOnlyList<ReplayHoldLine> Lines, DateTimeOffset ReceivedAt);

public sealed record ReplayHoldLine(string Sku, int Quantity, string? Note);

public sealed record SettleInvoiceRequest(string Id, IReadOnlyList<SettleInvoiceLine> Lines, DateTimeOffset ReceivedAt);

public sealed record SettleInvoiceLine(string Sku, int Quantity, string? Note);
