using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Coraclegate.Models;

public sealed record ReplayInvoiceRequest(string Id, IReadOnlyList<ReplayInvoiceLine> Lines, DateTimeOffset ReceivedAt);

public sealed record ReplayInvoiceLine(string Sku, int Quantity, string? Note);

public sealed record PriceHoldRequest(string Id, IReadOnlyList<PriceHoldLine> Lines, DateTimeOffset ReceivedAt);

public sealed record PriceHoldLine(string Sku, int Quantity, string? Note);

public sealed record SplitSlotRequest(string Id, IReadOnlyList<SplitSlotLine> Lines, DateTimeOffset ReceivedAt);

public sealed record SplitSlotLine(string Sku, int Quantity, string? Note);
