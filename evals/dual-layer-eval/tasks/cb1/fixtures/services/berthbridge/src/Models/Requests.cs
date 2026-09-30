using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Berthbridge.Models;

public sealed record SettleManifestRequest(
    string Id,
    IReadOnlyList<SettleManifestLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record SettleManifestLine(string Sku, int Quantity, string? Note);

public sealed record PriceVoyageRequest(string Id, IReadOnlyList<PriceVoyageLine> Lines, DateTimeOffset ReceivedAt);

public sealed record PriceVoyageLine(string Sku, int Quantity, string? Note);

public sealed record ValidateDocketRequest(
    string Id,
    IReadOnlyList<ValidateDocketLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ValidateDocketLine(string Sku, int Quantity, string? Note);
