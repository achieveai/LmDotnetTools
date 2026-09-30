using System;
using System.Collections.Generic;

namespace Quillfeather.Services.Tallyyard.Models;

public sealed record AllocateSlotRequest(string Id, IReadOnlyList<AllocateSlotLine> Lines, DateTimeOffset ReceivedAt);

public sealed record AllocateSlotLine(string Sku, int Quantity, string? Note);

public sealed record ValidateInvoiceRequest(
    string Id,
    IReadOnlyList<ValidateInvoiceLine> Lines,
    DateTimeOffset ReceivedAt
);

public sealed record ValidateInvoiceLine(string Sku, int Quantity, string? Note);

public sealed record SettlePalletRequest(string Id, IReadOnlyList<SettlePalletLine> Lines, DateTimeOffset ReceivedAt);

public sealed record SettlePalletLine(string Sku, int Quantity, string? Note);
