namespace ForEach.Infrastructure.External.eFaktura.Purchase;

/// <summary>
/// The head of an incoming invoice, read out of the UBL somebody else wrote.
///
/// Deliberately the header and not the lines. The inbox exists so a person can see what arrived,
/// recognise the supplier, and decide whether to accept it — and every one of those is answered by
/// the top of the document. Lines are what the *booking* needs, and the booking is a separate
/// decision made against a purchase order rather than against the XML.
///
/// Every field is nullable and nothing here refuses a document. This is somebody else's XML: a
/// supplier who omits a field we consider essential has still sent us an invoice, and an inbox row
/// that failed to appear because a tag was missing is worse than one showing a blank — the invoice
/// exists on SEF either way, and the clock on accepting it is running.
/// </summary>
/// <param name="IsCreditNote">
/// True for a credit note — UBL type code 381, or a CreditNote root element. Worth knowing at
/// the inbox rather than at booking: a knjižno odobrenje reduces what we owe, and one that
/// looked like an invoice in the list would be paid.
/// </param>
/// <param name="Number">The supplier's invoice number.</param>
/// <param name="IssueDate">When the supplier issued it.</param>
/// <param name="DueDate">The payment deadline, or null.</param>
/// <param name="SupplierName">The supplier's registered name.</param>
/// <param name="SupplierVatNumber">The supplier's PIB.</param>
/// <param name="SupplierRegistrationNumber">The supplier's matični broj.</param>
/// <param name="BuyerVatNumber">The buyer's PIB, which should be ours.</param>
/// <param name="Currency">ISO 4217 code the invoice is priced in.</param>
/// <param name="TotalWithoutVat">Total before tax.</param>
/// <param name="TotalVat">Total tax.</param>
/// <param name="TotalWithVat">Total payable.</param>
public sealed record SefPurchaseInvoiceHeader(
    string? Number,
    DateOnly? IssueDate,
    DateOnly? DueDate,
    string? SupplierName,
    string? SupplierVatNumber,
    string? SupplierRegistrationNumber,
    string? BuyerVatNumber,
    string? Currency,
    decimal? TotalWithoutVat,
    decimal? TotalVat,
    decimal? TotalWithVat,

    bool IsCreditNote);
