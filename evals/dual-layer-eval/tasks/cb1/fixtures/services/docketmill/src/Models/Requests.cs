using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Docketmill.Models;

public sealed record DispatchCrateRequest(string Id, IReadOnlyList<DispatchCrateLine> Lines, DateTimeOffset ReceivedAt);

public sealed record DispatchCrateLine(string Sku, int Quantity, string? Note);

public sealed record RoutePalletRequest(string Id, IReadOnlyList<RoutePalletLine> Lines, DateTimeOffset ReceivedAt);

public sealed record RoutePalletLine(string Sku, int Quantity, string? Note);

public sealed record TagConsignmentRequest(
    string Id,
    IReadOnlyList<TagConsignmentLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record TagConsignmentLine(string Sku, int Quantity, string? Note);
