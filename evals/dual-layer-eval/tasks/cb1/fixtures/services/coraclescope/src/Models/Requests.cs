using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Coraclescope.Models;

public sealed record PricePalletRequest(string Id, IReadOnlyList<PricePalletLine> Lines, DateTimeOffset ReceivedAt);

public sealed record PricePalletLine(string Sku, int Quantity, string? Note);

public sealed record SettleBatchRequest(string Id, IReadOnlyList<SettleBatchLine> Lines, DateTimeOffset ReceivedAt);

public sealed record SettleBatchLine(string Sku, int Quantity, string? Note);

public sealed record ValidateDocketRequest(
    string Id,
    IReadOnlyList<ValidateDocketLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ValidateDocketLine(string Sku, int Quantity, string? Note);
