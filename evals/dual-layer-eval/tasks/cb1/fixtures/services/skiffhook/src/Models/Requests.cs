using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Skiffhook.Models;

public sealed record AllocateReceiptRequest(
    string Id,
    IReadOnlyList<AllocateReceiptLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record AllocateReceiptLine(string Sku, int Quantity, string? Note);

public sealed record DispatchVoyageRequest(
    string Id,
    IReadOnlyList<DispatchVoyageLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record DispatchVoyageLine(string Sku, int Quantity, string? Note);

public sealed record ReleaseVoyageRequest(string Id, IReadOnlyList<ReleaseVoyageLine> Lines, DateTimeOffset ReceivedAt);

public sealed record ReleaseVoyageLine(string Sku, int Quantity, string? Note);
