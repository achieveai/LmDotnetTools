using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Skiffworks.Models;

public sealed record TagHoldRequest(string Id, IReadOnlyList<TagHoldLine> Lines, DateTimeOffset ReceivedAt);

public sealed record TagHoldLine(string Sku, int Quantity, string? Note);

public sealed record AllocateSlotRequest(string Id, IReadOnlyList<AllocateSlotLine> Lines, DateTimeOffset ReceivedAt);

public sealed record AllocateSlotLine(string Sku, int Quantity, string? Note);

public sealed record AuditCrateRequest(string Id, IReadOnlyList<AuditCrateLine> Lines, DateTimeOffset ReceivedAt);

public sealed record AuditCrateLine(string Sku, int Quantity, string? Note);
