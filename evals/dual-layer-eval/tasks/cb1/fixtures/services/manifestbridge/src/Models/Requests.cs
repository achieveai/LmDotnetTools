using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Manifestbridge.Models;

public sealed record DispatchVoyageRequest(
    string Id,
    IReadOnlyList<DispatchVoyageLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record DispatchVoyageLine(string Sku, int Quantity, string? Note);

public sealed record RouteConsignmentRequest(
    string Id,
    IReadOnlyList<RouteConsignmentLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record RouteConsignmentLine(string Sku, int Quantity, string? Note);

public sealed record ReleaseVoyageRequest(string Id, IReadOnlyList<ReleaseVoyageLine> Lines, DateTimeOffset ReceivedAt);

public sealed record ReleaseVoyageLine(string Sku, int Quantity, string? Note);
