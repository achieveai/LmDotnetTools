using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Skiffgate.Models;

public sealed record AuditPalletRequest(string Id, IReadOnlyList<AuditPalletLine> Lines, DateTimeOffset ReceivedAt);

public sealed record AuditPalletLine(string Sku, int Quantity, string? Note);

public sealed record PricePalletRequest(string Id, IReadOnlyList<PricePalletLine> Lines, DateTimeOffset ReceivedAt);

public sealed record PricePalletLine(string Sku, int Quantity, string? Note);

public sealed record DispatchBatchRequest(string Id, IReadOnlyList<DispatchBatchLine> Lines, DateTimeOffset ReceivedAt);

public sealed record DispatchBatchLine(string Sku, int Quantity, string? Note);
