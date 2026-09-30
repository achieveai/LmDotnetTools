using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Capstanloom.Models;

public sealed record MergeVoyageRequest(string Id, IReadOnlyList<MergeVoyageLine> Lines, DateTimeOffset ReceivedAt);

public sealed record MergeVoyageLine(string Sku, int Quantity, string? Note);

public sealed record ReplayBookingRequest(string Id, IReadOnlyList<ReplayBookingLine> Lines, DateTimeOffset ReceivedAt);

public sealed record ReplayBookingLine(string Sku, int Quantity, string? Note);

public sealed record SplitCrateRequest(string Id, IReadOnlyList<SplitCrateLine> Lines, DateTimeOffset ReceivedAt);

public sealed record SplitCrateLine(string Sku, int Quantity, string? Note);
