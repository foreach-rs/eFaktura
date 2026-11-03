using System.Globalization;
using System.Xml.Linq;

namespace ForEach.Infrastructure.External.eFaktura.Purchase;

/// <summary>
/// Reads the head of an incoming UBL invoice.
///
/// The mirror of <see cref="Ubl.SefInvoiceUblWriter"/> and a different job in one important way:
/// the writer produces XML this code controls, and this reads XML somebody else's software wrote.
/// Everything here is therefore defensive by construction — no exception escapes, no field is
/// required, and a document that cannot be parsed at all comes back as a header full of nulls
/// rather than as a failure. The invoice exists on SEF whether or not we can read it, and the
/// deadline to accept or reject it is running: a row somebody can see and open is worth more than
/// a correct refusal to show one.
///
/// Pure — a string in, a record out — so every shape a supplier might send can be asserted without
/// a network or an API key. That matters more here than on the writing side, because the inputs
/// are not ours to fix.
/// </summary>
public static class SefInvoiceUblReader
{
    private static readonly XNamespace Cac =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    private static readonly XNamespace Cbc =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    /// <summary>UBL type codes: 380 is a commercial invoice, 381 a credit note.</summary>
    private const string CreditNoteTypeCode = "381";

    /// <summary>
    /// The header, as far as it can be read. Never throws and never returns null.
    /// </summary>
    public static SefPurchaseInvoiceHeader Read(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Empty;

        XElement root;

        try
        {
            root = XDocument.Parse(xml).Root ?? throw new InvalidOperationException();
        }
        catch (Exception exception)
            when (exception is System.Xml.XmlException or InvalidOperationException)
        {
            // Not XML at all, or an empty document. Silently empty rather than thrown: the caller
            // is a polling job storing what arrived, and one unreadable invoice must not stop it
            // from storing the other forty.
            return Empty;
        }

        var supplier = root.Element(Cac + "AccountingSupplierParty")?.Element(Cac + "Party");
        var buyer = root.Element(Cac + "AccountingCustomerParty")?.Element(Cac + "Party");
        var totals = root.Element(Cac + "LegalMonetaryTotal");

        return new SefPurchaseInvoiceHeader(
            Text(root.Element(Cbc + "ID")),
            Date(root.Element(Cbc + "IssueDate")),

            // UBL 2.1 puts the due date on the Invoice itself, as cbc:DueDate — that is where the
            // Serbian CIUS and this library's own writer put it. PaymentMeans/PaymentDueDate is
            // the older place and some suppliers still use it, so it is the fallback; a credit
            // note has neither.
            Date(root.Element(Cbc + "DueDate"))
            ?? Date(root.Element(Cac + "PaymentMeans")?.Element(Cbc + "PaymentDueDate")),
            PartyName(supplier),
            CompanyId(supplier, "VA"),
            CompanyId(supplier, null),
            CompanyId(buyer, "VA"),
            Text(root.Element(Cbc + "DocumentCurrencyCode")),
            Money(totals?.Element(Cbc + "TaxExclusiveAmount")),
            Money(root.Element(Cac + "TaxTotal")?.Element(Cbc + "TaxAmount")),
            Money(totals?.Element(Cbc + "TaxInclusiveAmount")),
            IsCreditNote(root));
    }

    private static SefPurchaseInvoiceHeader Empty =>
        new(null, null, null, null, null, null, null, null, null, null, null, false);

    /// <summary>
    /// Both ways a credit note announces itself, because suppliers use both: the root element is
    /// `CreditNote` on a document built to the credit-note schema, and the type code is 381 on one
    /// built to the invoice schema. Reading only the code misses half of them.
    /// </summary>
    private static bool IsCreditNote(XElement root) =>
        root.Name.LocalName == "CreditNote"
        || Text(root.Element(Cbc + "InvoiceTypeCode")) == CreditNoteTypeCode;

    private static string? PartyName(XElement? party) =>
        // The registered name first, then the trading one. A Serbian invoice carries
        // PartyLegalEntity with the name in the register, which is what somebody matches against
        // the contact list — PartyName is whatever the supplier calls itself on the letterhead.
        Text(party?.Element(Cac + "PartyLegalEntity")?.Element(Cbc + "RegistrationName"))
        ?? Text(party?.Element(Cac + "PartyName")?.Element(Cbc + "Name"));

    /// <summary>
    /// A company identifier by scheme. `VA` is the tax number (PIB); the scheme-less
    /// CompanyID under PartyLegalEntity is the registration number (matični broj).
    /// </summary>
    private static string? CompanyId(XElement? party, string? scheme)
    {
        if (party is null) return null;

        if (scheme is not null)
        {
            var byScheme = party
                .Elements(Cac + "PartyTaxScheme")
                .Select(t => t.Element(Cbc + "CompanyID"))
                .FirstOrDefault(id => (string?)id?.Attribute("schemeID") == scheme
                                      || id?.Parent?.Element(Cac + "TaxScheme")
                                          ?.Element(Cbc + "ID")?.Value == scheme);

            // Falling back to the first tax scheme when nothing is labelled. Serbian invoices
            // carry exactly one, and refusing to read an unlabelled PIB would blank the field
            // somebody uses to recognise the supplier.
            return Text(byScheme)
                   ?? Text(party.Elements(Cac + "PartyTaxScheme")
                       .Select(t => t.Element(Cbc + "CompanyID"))
                       .FirstOrDefault());
        }

        return Text(party.Element(Cac + "PartyLegalEntity")?.Element(Cbc + "CompanyID"));
    }

    private static string? Text(XElement? element) =>
        string.IsNullOrWhiteSpace(element?.Value) ? null : element.Value.Trim();

    private static DateOnly? Date(XElement? element) =>
        Text(element) is { } value
        && DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Invariant on purpose, and this is the one that goes wrong quietly. UBL amounts are written
    /// with a point; parsed under a Serbian culture, "1234.56" becomes one and a quarter million,
    /// and the inbox shows a supplier invoice a thousand times too large.
    /// </summary>
    private static decimal? Money(XElement? element) =>
        Text(element) is { } value
        && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
