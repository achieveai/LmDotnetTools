using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Bollardbridge.Models;

public sealed record ValidateCrateRequest(string Id, IReadOnlyList<ValidateCrateLine> Lines, DateTimeOffset ReceivedAt);

public sealed record ValidateCrateLine(string Sku, int Quantity, string? Note);

public sealed record RouteBatchRequest(string Id, IReadOnlyList<RouteBatchLine> Lines, DateTimeOffset ReceivedAt);

public sealed record RouteBatchLine(string Sku, int Quantity, string? Note);

public sealed record SplitConsignmentRequest(
    string Id,
    IReadOnlyList<SplitConsignmentLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record SplitConsignmentLine(string Sku, int Quantity, string? Note);
