using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Ferryrelay.Models;

public sealed record DispatchPalletRequest(
    string Id,
    IReadOnlyList<DispatchPalletLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record DispatchPalletLine(string Sku, int Quantity, string? Note);

public sealed record RouteBookingRequest(string Id, IReadOnlyList<RouteBookingLine> Lines, DateTimeOffset ReceivedAt);

public sealed record RouteBookingLine(string Sku, int Quantity, string? Note);

public sealed record ReleaseInvoiceRequest(
    string Id,
    IReadOnlyList<ReleaseInvoiceLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ReleaseInvoiceLine(string Sku, int Quantity, string? Note);
