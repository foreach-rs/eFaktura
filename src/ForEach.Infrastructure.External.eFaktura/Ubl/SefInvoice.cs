namespace ForEach.Infrastructure.External.eFaktura.Ubl;

/// <summary>
/// Which document this is.
///
/// Three of these five are shapes the writer can produce. The other two —
/// <see cref="DebitNote"/> and the advance pair — exist so a host can hand one over and be refused
/// *by name* rather than have it filed as something else. See the writer for why that refusal is
/// deliberate: a document the tax authority accepts and records as the wrong kind is worse than
/// one it rejects.
/// </summary>
public enum SefInvoiceType
{
    /// <summary>An ordinary invoice.</summary>
    Invoice,
    /// <summary>A credit note: reduces what an earlier invoice asked for.</summary>
    CreditNote,
    /// <summary>A debit note. Refused by the writer, by name.</summary>
    DebitNote,

    /// <summary>
    /// Avansni račun. UN/CEFACT 386, prepayment invoice: a declaration on money received rather
    /// than on goods delivered.
    /// </summary>
    AdvanceInvoice,

    /// <summary>
    /// Konačni račun: an ordinary supply that deducts advances already invoiced. It is the
    /// deduction that makes it a shape of its own — filed as a plain invoice it would declare the
    /// full supply and take nothing back, so the tax on the advance would be paid twice.
    ///
    /// UBL expresses the deduction as <c>cac:PrepaidPayment</c>, and the payable amount falls by
    /// it. The advances themselves are named in <c>cac:BillingReference</c>, which is what lets
    /// the authority match the deduction to declarations it already holds.
    /// </summary>
    FinalInvoice
}

/// <summary>
/// The UN/CEFACT tax category, which is not the same thing as the rate.
///
/// A reduced rate is still <see cref="Standard"/>; what makes it reduced is the percentage beside
/// it. Callers that map a domestic rate enum onto this are making a declaration to a tax
/// authority, so the enum is closed and the writer refuses anything it cannot code.
/// </summary>
public enum SefVatCategory
{
    /// <summary>Code S. Every rate that is actually charged, standard and reduced alike.</summary>
    Standard,

    /// <summary>Code Z. Zero-rated: taxable, at nothing.</summary>
    Zero,

    /// <summary>Code E. Exempt, and the one category that must say on what basis.</summary>
    Exempt
}

/// <summary>
/// A seller or a buyer, as the document names them.
/// </summary>
/// <param name="CountryCode">
/// ISO 3166-1 alpha-2, and the party's OWN. It prefixes the tax identifier, so getting it from
/// the issuer rather than the party files a Serbian PIB that belongs to somebody else.
/// </param>
/// <param name="Name">The registered name. Required.</param>
/// <param name="VatNumber">PIB or foreign tax identifier, without the country prefix, or null.</param>
/// <param name="RegistrationNumber">Matični broj, or null.</param>
/// <param name="Address">Street address, or null.</param>
/// <param name="City">City, or null.</param>
/// <param name="ZipCode">Postal code, or null.</param>
public sealed record SefParty(
    string Name,
    string? VatNumber,
    string? RegistrationNumber,
    string? Address,
    string? City,
    string? ZipCode,
    string CountryCode);

/// <summary>
/// One line, with its money already computed.
/// </summary>
/// <param name="Unit">
/// As the host spells it. The writer translates it into a UN/ECE Recommendation 20 code and
/// refuses a spelling it does not know — declaring five hundred kilograms as five hundred items is
/// arithmetically silent and impossible to withdraw.
/// </param>
/// <param name="Net">
/// The line total after discount, computed by the host. Nothing here multiplies or rounds: a
/// writer that recalculated would round independently of the document it is filing, and a
/// one-dinar difference between the two is a rejection.
/// </param>
/// <param name="Description">What was supplied, as the host names it.</param>
/// <param name="Quantity">How much was supplied.</param>
/// <param name="UnitPrice">Price per unit before discount.</param>
/// <param name="DiscountPercent">Discount on the line, in percent.</param>
/// <param name="VatCategory">The UN/CEFACT tax category of the line.</param>
/// <param name="VatPercentage">The rate charged, in percent.</param>
/// <param name="VatExemptionReason">The legal basis for an exempt line, or null.</param>
/// <param name="Vat">The line's tax, computed by the host.</param>
public sealed record SefInvoiceLine(
    string Description,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    SefVatCategory VatCategory,
    decimal VatPercentage,
    string? VatExemptionReason,
    decimal Net,
    decimal Vat);

/// <summary>
/// The tax stated a second time, in the tax currency, for a document priced in another one.
/// </summary>
/// <param name="VatInTaxCurrency">
/// The host's stored snapshot of the converted tax, never a conversion done by the writer.
/// </param>
/// <param name="Rate">The published rate, as published.</param>
/// <param name="RateUnit">
/// How many units of the foreign currency the rate is quoted for — 1 for most, 100 for JPY and
/// HUF. The writer divides by it, because UBL states the rate per single unit and filing the raw
/// figure would overstate the conversion a hundredfold.
/// </param>
/// <param name="RateDate">The date the published rate applies to.</param>
public sealed record SefTaxConversion(
    decimal VatInTaxCurrency,
    decimal Rate,
    decimal RateUnit,
    DateOnly RateDate);

/// <summary>
/// Everything the writer needs, and nothing about the host that produced it.
///
/// This is the type that makes the library reusable: the writer cannot take another product's
/// invoice DTO, so the host maps onto this — which is also where the host's own enums turn into
/// UBL codes, in its code, under its tests.
/// </summary>
/// <param name="TaxPointDate">
/// Datum prometa: when the supply happened, which for tax purposes matters more than when the
/// invoice was written.
/// </param>
/// <param name="DueDate">
/// Null on a credit note, which carries no payment deadline of its own — it reduces what the
/// original asked for.
/// </param>
/// <param name="TotalNet">
/// Totals as the document states them. The writer checks that the lines add up to them and
/// refuses if they do not, rather than quietly filing whichever of the two is wrong.
/// </param>
/// <param name="PrepaidInvoiceNumbers">
/// Advances already invoiced and declared, deducted by this document. Empty on every kind but
/// a konačni račun.
///
/// The numbers, not the ids: the authority holds those documents under the numbers they were
/// filed with, and a reference by anything else is a reference it cannot resolve.
/// </param>
/// <param name="PrepaidAmount">
/// The gross sum of those advances. Stated separately rather than derived, because the
/// document deducts a figure somebody agreed rather than a sum this library recomputes — and
/// a deduction that disagreed with the advances it names is a rejection.
/// </param>
/// <param name="Number">The document number, by which SEF knows it.</param>
/// <param name="Type">Which kind of document this is.</param>
/// <param name="IssueDate">When the document was issued.</param>
/// <param name="CurrencyCode">ISO 4217 code of the currency the document is priced in.</param>
/// <param name="Seller">Who issues the document.</param>
/// <param name="Buyer">Who it is addressed to.</param>
/// <param name="Lines">What is being invoiced. At least one.</param>
/// <param name="TotalVat">Total tax, as the document states it.</param>
/// <param name="TotalGross">Total payable, as the document states it.</param>
/// <param name="BankAccount">Where to pay, or null.</param>
/// <param name="Note">Free text carried on the document, or null.</param>
/// <param name="CorrectsInvoiceNumber">On a credit note, the number of the invoice it corrects.</param>
/// <param name="Conversion">The tax restated in the tax currency, when the document is priced in another one.</param>
public sealed record SefInvoice(
    string Number,
    SefInvoiceType Type,
    DateOnly IssueDate,
    DateOnly? DueDate,
    DateOnly TaxPointDate,
    string CurrencyCode,
    SefParty Seller,
    SefParty Buyer,
    IReadOnlyList<SefInvoiceLine> Lines,
    decimal TotalNet,
    decimal TotalVat,
    decimal TotalGross,
    string? BankAccount = null,
    string? Note = null,
    string? CorrectsInvoiceNumber = null,
    SefTaxConversion? Conversion = null,

    IReadOnlyList<string>? PrepaidInvoiceNumbers = null,

    decimal PrepaidAmount = 0m);

/// <summary>
/// What is true of every document this host files rather than of one invoice.
/// </summary>
/// <param name="CustomizationId">
/// The CIUS identifier. Versioned by the tax authority and configured by the host, because the day
/// it changes is a configuration change and not a release of this library.
/// </param>
/// <param name="TaxCurrencyCode">
/// The currency tax is accounted in — the dinar, in Serbia. Stated on the document only when it
/// differs from the currency the document is priced in: emitting it on a domestic invoice invites
/// a validation error about a conversion that never happened.
/// </param>
public sealed record SefUblOptions(string CustomizationId, string TaxCurrencyCode = "RSD");
