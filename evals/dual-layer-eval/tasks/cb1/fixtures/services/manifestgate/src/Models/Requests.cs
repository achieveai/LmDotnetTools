using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Manifestgate.Models;

public sealed record SettleVoyageRequest(string Id, IReadOnlyList<SettleVoyageLine> Lines, DateTimeOffset ReceivedAt);

public sealed record SettleVoyageLine(string Sku, int Quantity, string? Note);

public sealed record RouteSlotRequest(string Id, IReadOnlyList<RouteSlotLine> Lines, DateTimeOffset ReceivedAt);

public sealed record RouteSlotLine(string Sku, int Quantity, string? Note);

public sealed record DispatchConsignmentRequest(
    string Id,
    IReadOnlyList<DispatchConsignmentLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record DispatchConsignmentLine(string Sku, int Quantity, string? Note);
