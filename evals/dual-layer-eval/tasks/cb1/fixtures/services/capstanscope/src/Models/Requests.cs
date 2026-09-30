using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Capstanscope.Models;

public sealed record RouteDocketRequest(string Id, IReadOnlyList<RouteDocketLine> Lines, DateTimeOffset ReceivedAt);

public sealed record RouteDocketLine(string Sku, int Quantity, string? Note);

public sealed record StageBookingRequest(string Id, IReadOnlyList<StageBookingLine> Lines, DateTimeOffset ReceivedAt);

public sealed record StageBookingLine(string Sku, int Quantity, string? Note);

public sealed record AuditManifestRequest(string Id, IReadOnlyList<AuditManifestLine> Lines, DateTimeOffset ReceivedAt);

public sealed record AuditManifestLine(string Sku, int Quantity, string? Note);
