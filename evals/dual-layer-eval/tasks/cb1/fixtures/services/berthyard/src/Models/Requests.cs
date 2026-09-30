using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Berthyard.Models;

public sealed record DispatchVoyageRequest(
    string Id,
    IReadOnlyList<DispatchVoyageLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record DispatchVoyageLine(string Sku, int Quantity, string? Note);

public sealed record ReleaseDocketRequest(string Id, IReadOnlyList<ReleaseDocketLine> Lines, DateTimeOffset ReceivedAt);

public sealed record ReleaseDocketLine(string Sku, int Quantity, string? Note);

public sealed record ValidateBookingRequest(
    string Id,
    IReadOnlyList<ValidateBookingLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ValidateBookingLine(string Sku, int Quantity, string? Note);
