using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Berthworks.Models;

public sealed record DispatchDocketRequest(
    string Id,
    IReadOnlyList<DispatchDocketLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record DispatchDocketLine(string Sku, int Quantity, string? Note);

public sealed record ReleaseManifestRequest(
    string Id,
    IReadOnlyList<ReleaseManifestLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ReleaseManifestLine(string Sku, int Quantity, string? Note);

public sealed record StageVoyageRequest(string Id, IReadOnlyList<StageVoyageLine> Lines, DateTimeOffset ReceivedAt);

public sealed record StageVoyageLine(string Sku, int Quantity, string? Note);
