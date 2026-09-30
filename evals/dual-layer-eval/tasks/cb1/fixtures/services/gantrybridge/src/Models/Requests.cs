using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Gantrybridge.Models;

public sealed record PriceManifestRequest(string Id, IReadOnlyList<PriceManifestLine> Lines, DateTimeOffset ReceivedAt);

public sealed record PriceManifestLine(string Sku, int Quantity, string? Note);

public sealed record SettlePalletRequest(string Id, IReadOnlyList<SettlePalletLine> Lines, DateTimeOffset ReceivedAt);

public sealed record SettlePalletLine(string Sku, int Quantity, string? Note);

public sealed record AuditConsignmentRequest(
    string Id,
    IReadOnlyList<AuditConsignmentLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record AuditConsignmentLine(string Sku, int Quantity, string? Note);
