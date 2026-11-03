using System.Globalization;
using System.Xml.Linq;

namespace ForEach.Infrastructure.External.eFaktura.Ubl;

/// <summary>
/// Writes the UBL 2.1 invoice SEF expects, conforming to the Serbian CIUS.
///
/// ⚠️ The customization identifier, the tax category codes and the exemption-reason handling
/// reflect the specification as understood when this was written and have not been validated
/// against a live SEF instance. Validate the generated XML against the current schema and
/// schematron before the first real submission — a rejection after a number has been allocated is
/// expensive to unwind.
///
/// A pure function on purpose: it takes a model and returns a string, so the whole document can be
/// asserted in unit tests without a network, a database or an API key.
///
/// It never computes money. Every amount arrives already rounded by whoever issued the document,
/// and this class formats and cross-checks them — because a writer that recalculated would round
/// independently of the invoice it is filing, and a one-dinar disagreement between the submission
/// and the document is a rejection.
/// </summary>
public static class SefInvoiceUblWriter
{
    private static readonly XNamespace Invoice = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace CreditNote = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    /// <summary>
    /// UN/CEFACT 1001 document type codes, as the Serbian CIUS uses them.
    ///
    /// **A konačni račun is a 380 like any other invoice.** What makes it final is the prepayment
    /// it deducts, not a code of its own — the deduction is the <c>cbc:PrepaidAmount</c> in
    /// <c>cac:LegalMonetaryTotal</c>, which the payable amount falls by, and the advances are
    /// named in <c>cac:BillingReference</c>.
    /// </summary>
    private const string InvoiceTypeCode = "380";
    private const string CreditNoteTypeCode = "381";
    private const string DebitNoteTypeCode = "383";
    private const string AdvanceInvoiceTypeCode = "386";

    /// <summary>
    /// The document, or the reasons it cannot be written.
    ///
    /// A refusal here is a <see cref="SefFailureKind.CallerMistake"/> and carries every problem
    /// found rather than the first: whoever fixes it is looking at one invoice, and telling them
    /// about one missing field at a time turns a correction into four.
    /// </summary>
    public static SefResult<string> Write(SefInvoice invoice, SefUblOptions options)
    {
        var problems = Check(invoice, options);

        if (problems.Count > 0)
        {
            return SefResult<string>.Fail(SefError.Local(
                "Dokument nije moguće pripremiti za SEF.", problems));
        }

        var isCreditNote = invoice.Type == SefInvoiceType.CreditNote;

        var root = isCreditNote ? CreditNote : Invoice;
        var rootName = isCreditNote ? "CreditNote" : "Invoice";

        var document = new XElement(root + rootName,
            new XAttribute(XNamespace.Xmlns + "cac", Cac),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            new XElement(Cbc + "CustomizationID", options.CustomizationId),
            new XElement(Cbc + "ID", invoice.Number),
            new XElement(Cbc + "IssueDate", Iso(invoice.IssueDate)));

        // A credit note carries no due date of its own; it reduces what the original invoice
        // asked for, and stating a payment deadline on it would be meaningless.
        if (!isCreditNote)
            document.Add(new XElement(Cbc + "DueDate", Iso(invoice.DueDate!.Value)));

        // A debit note is an *Invoice* document with type code 383. UBL has no DebitNote root:
        // the thing a knjižno zaduženje does is charge more, which is what an invoice does, and
        // the code is what says it corrects an earlier one rather than declaring a new supply.
        document.Add(new XElement(
            Cbc + (isCreditNote ? "CreditNoteTypeCode" : "InvoiceTypeCode"),
            TypeCodeFor(invoice.Type)));

        // Directly after the type code in both schemas, ahead of the currency codes: a Note
        // written further down is out of sequence and the document fails schema validation.
        if (!string.IsNullOrWhiteSpace(invoice.Note))
            document.Add(new XElement(Cbc + "Note", invoice.Note));

        document.Add(new XElement(Cbc + "DocumentCurrencyCode", invoice.CurrencyCode));

        // The currency the tax is accounted in. Stated only when the document is not already in
        // it: emitting it on a domestic invoice would be redundant at best and, on a CIUS that
        // reconciles the two currencies against each other, an invitation to a validation error
        // about a conversion that never happened.
        if (IsForeignCurrency(invoice, options))
            document.Add(new XElement(Cbc + "TaxCurrencyCode", options.TaxCurrencyCode));

        // Datum prometa. Carried as an invoice period rather than a single date because that is
        // how the Serbian CIUS expresses the supply date on a document that has no line-level
        // period of its own.
        document.Add(new XElement(Cac + "InvoicePeriod",
            new XElement(Cbc + "StartDate", Iso(invoice.TaxPointDate)),
            new XElement(Cbc + "EndDate", Iso(invoice.TaxPointDate))));

        // The document being corrected, on both kinds of correction. A knjižno zaduženje needs
        // this as much as a knjižno odobrenje does — without it the authority holds a 383 that
        // corrects nothing it can name, which is a supply declared twice.
        if (invoice.Type is SefInvoiceType.CreditNote or SefInvoiceType.DebitNote
            && !string.IsNullOrWhiteSpace(invoice.CorrectsInvoiceNumber))
        {
            document.Add(new XElement(Cac + "BillingReference",
                new XElement(Cac + "InvoiceDocumentReference",
                    new XElement(Cbc + "ID", invoice.CorrectsInvoiceNumber))));
        }

        // One reference per advance being deducted. Several rather than one, because a supply is
        // routinely prepaid in instalments and each advance was declared as its own document —
        // collapsing them into a single reference would deduct a sum against a document that
        // never carried it.
        foreach (var advance in invoice.PrepaidInvoiceNumbers ?? [])
        {
            document.Add(new XElement(Cac + "BillingReference",
                new XElement(Cac + "InvoiceDocumentReference",
                    new XElement(Cbc + "ID", advance))));
        }

        document.Add(Party(Cac + "AccountingSupplierParty", invoice.Seller));
        document.Add(Party(Cac + "AccountingCustomerParty", invoice.Buyer));

        if (!string.IsNullOrWhiteSpace(invoice.BankAccount))
        {
            document.Add(new XElement(Cac + "PaymentMeans",
                // 30 = credit transfer. A cash sale does not go through SEF.
                new XElement(Cbc + "PaymentMeansCode", "30"),
                new XElement(Cbc + "PaymentID", invoice.Number),
                new XElement(Cac + "PayeeFinancialAccount",
                    new XElement(Cbc + "ID", invoice.BankAccount))));
        }

        // The tax-currency half, on a foreign-currency invoice only. Both elements are omitted
        // entirely on a domestic one: there is no conversion to declare, and declaring one would
        // assert an exchange that never took place.
        //
        // The rate comes BEFORE the first TaxTotal in both schemas, while the second TaxTotal
        // follows the first; written the other way round the document is out of sequence.
        if (invoice.Conversion is { } conversion)
            document.Add(TaxExchangeRate(invoice, conversion, options));

        document.Add(TaxTotal(invoice));

        if (invoice.Conversion is { } taxCurrencyConversion)
            document.Add(TaxTotalInTaxCurrency(taxCurrencyConversion, options));

        document.Add(MonetaryTotal(invoice));

        var lineNumber = 1;
        foreach (var line in invoice.Lines)
            document.Add(Line(line, lineNumber++, invoice.CurrencyCode, isCreditNote));

        var xml = new XDocument(new XDeclaration("1.0", "UTF-8", null), document);

        // XDocument.ToString() drops the declaration, so it is written out here.
        return SefResult<string>.Ok(xml.Declaration + Environment.NewLine + xml);
    }

    /// <summary>
    /// UN/CEFACT 1001, per kind.
    ///
    /// **Written from the published CIUS rather than from a sandbox exchange.** The codes and the
    /// prepayment shape are UBL 2.1 and the Serbian CIUS as documented; what a sandbox settles is
    /// whether *this host's* documents pass validation, and that is a verification step rather
    /// than a design question. Until one has run, the writer tests are what pin the shape, so the
    /// first sandbox rejection shows as a diff against a known document rather than as a mystery.
    /// </summary>
    private static string TypeCodeFor(SefInvoiceType type) => type switch
    {
        SefInvoiceType.CreditNote => CreditNoteTypeCode,
        SefInvoiceType.DebitNote => DebitNoteTypeCode,
        SefInvoiceType.AdvanceInvoice => AdvanceInvoiceTypeCode,

        // A konačni račun is an ordinary invoice that deducts. See the constants above.
        _ => InvoiceTypeCode
    };

    /// <summary>
    /// Everything wrong with the document, checked before a single element is written.
    ///
    /// These are not schema checks — SEF does those, and does them better. They are the mistakes
    /// whose cost is a document the tax authority accepts and records differently from what the
    /// customer was charged: totals that do not match the lines they are made of, a quantity in a
    /// unit nobody coded, an exemption claimed on no stated basis.
    /// </summary>
    private static List<string> Check(SefInvoice invoice, SefUblOptions options)
    {
        var problems = new List<string>();

        // The three correction-and-advance kinds are filed now rather than refused. What they
        // needed was never effort — it was the UBL shape, and that comes from the published CIUS:
        // a knjižno zaduženje is an Invoice with type code 383 referencing the document it
        // corrects, an avansni račun is a 386, and a konačni račun is an ordinary 380 whose
        // prepayment is expressed as `cbc:PrepaidAmount` in `cac:LegalMonetaryTotal`, beside
        // `cac:BillingReference` entries naming the advances. The sandbox settles whether *these*
        // documents validate, which is a verification step; the writer tests pin the shape until
        // it runs.
        //
        // What is checked here is what SEF would accept and record wrongly:

        // A correction that names nothing corrects nothing. SEF would take a 381 or a 383 with no
        // BillingReference and hold a document adjusting a supply it cannot identify — which reads
        // as a second supply, in whichever direction the correction went.
        if (invoice.Type is SefInvoiceType.CreditNote or SefInvoiceType.DebitNote
            && string.IsNullOrWhiteSpace(invoice.CorrectsInvoiceNumber))
        {
            problems.Add(
                "Knjižno odobrenje i knjižno zaduženje moraju da imenuju račun koji ispravljaju — "
                + "bez toga SEF beleži novi promet umesto ispravke postojećeg.");
        }

        // A deduction against nothing. The tax on those advances was declared under document
        // numbers the authority holds; deducting a sum without naming them asks it to give back
        // tax it cannot match to anything it received.
        if (invoice.PrepaidAmount > 0m && (invoice.PrepaidInvoiceNumbers ?? []).Count == 0)
        {
            problems.Add(
                "Konačni račun koji umanjuje za avans mora da imenuje avansne račune — odbitak "
                + "bez veze sa prijavljenim avansom SEF ne može da poveže.");
        }

        // And the reverse: references with nothing behind them would claim advances were deducted
        // when the payable amount says they were not.
        if (invoice.PrepaidAmount <= 0m && (invoice.PrepaidInvoiceNumbers ?? []).Count > 0)
        {
            problems.Add(
                "Navedeni su avansni računi, a iznos avansa je nula — dokument tvrdi odbitak koji "
                + "ne pravi.");
        }

        // More deducted than charged. The payable amount would go negative, which is not an
        // invoice — it is a refund, and a refund is a credit note.
        if (invoice.PrepaidAmount > invoice.TotalGross)
        {
            problems.Add(
                "Avans je veći od ukupnog iznosa računa — to nije konačni račun nego povraćaj, "
                + "koji ide kao knjižno odobrenje.");
        }

        // Prepayment belongs to a final invoice. On an advance itself it would deduct the advance
        // from the advance; on a credit note it is meaningless.
        if (invoice.PrepaidAmount > 0m && invoice.Type != SefInvoiceType.FinalInvoice)
        {
            problems.Add(
                "Avans se odbija samo na konačnom računu.");
        }

        // cbc:ID is how the tax authority knows this document from every other one. A blank number
        // here means a draft slipped through, and the alternative to refusing is filing a document
        // whose identifier is an empty string — which can never be referenced, credited or
        // stornoed.
        if (string.IsNullOrWhiteSpace(invoice.Number))
            problems.Add("Dokument bez broja ne može biti prijavljen na SEF.");

        if (invoice.Type != SefInvoiceType.CreditNote && invoice.DueDate is null)
            problems.Add("Faktura mora imati rok plaćanja.");

        if (invoice.Lines.Count == 0)
            problems.Add("Dokument bez stavki ne može biti prijavljen na SEF.");

        foreach (var line in invoice.Lines.Where(line => UblUnits.Code(line.Unit) is null))
        {
            problems.Add(
                $"Jedinica mere \"{line.Unit}\" nema UN/ECE oznaku (stavka: {line.Description}). "
                + $"Podržane: {UblUnits.Known}.");
        }

        // SEF refuses an exempt line with no stated basis, and refusing here says which line and
        // why — where SEF's own answer is a schema message about a missing element.
        foreach (var line in invoice.Lines.Where(line =>
                     line.VatCategory == SefVatCategory.Exempt
                     && string.IsNullOrWhiteSpace(line.VatExemptionReason)))
        {
            problems.Add(
                $"Stavka \"{line.Description}\" je oslobođena PDV-a bez navedenog osnova "
                + "oslobođenja.");
        }

        // The arithmetic a tax inspector re-runs. Compared exactly rather than with a tolerance:
        // both sides are already rounded to the currency, so a difference of a hundredth is a real
        // disagreement between the document and its own lines and not a floating-point artefact.
        var lineNet = invoice.Lines.Sum(line => line.Net);
        var lineVat = invoice.Lines.Sum(line => line.Vat);

        if (lineNet != invoice.TotalNet)
            problems.Add($"Zbir stavki ({Number(lineNet)}) se ne poklapa sa osnovicom dokumenta ({Number(invoice.TotalNet)}).");

        if (lineVat != invoice.TotalVat)
            problems.Add($"Zbir PDV-a po stavkama ({Number(lineVat)}) se ne poklapa sa PDV-om dokumenta ({Number(invoice.TotalVat)}).");

        if (invoice.TotalNet + invoice.TotalVat != invoice.TotalGross)
            problems.Add($"Ukupan iznos ({Number(invoice.TotalGross)}) nije zbir osnovice i PDV-a ({Number(invoice.TotalNet + invoice.TotalVat)}).");

        // A conversion on a document already in the tax currency, or a foreign-currency document
        // with none, are the two ways the pair of TaxTotals comes out meaning something nobody
        // intended.
        if (invoice.Conversion is not null && !IsForeignCurrency(invoice, options))
            problems.Add("Dokument je već u valuti poreza, pa preračun kursa nema smisla.");

        if (invoice.Conversion is { RateUnit: <= 0 })
            problems.Add("Kurs mora biti iskazan za pozitivan broj jedinica valute.");

        return problems;
    }

    private static bool IsForeignCurrency(SefInvoice invoice, SefUblOptions options) =>
        !string.Equals(invoice.CurrencyCode, options.TaxCurrencyCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One TaxSubtotal per rate. SEF reconciles the document rate by rate, so a single combined
    /// total is rejected as soon as more than one rate appears.
    /// </summary>
    private static XElement TaxTotal(SefInvoice invoice)
    {
        var taxTotal = new XElement(Cac + "TaxTotal",
            new XElement(Cbc + "TaxAmount", Amount(invoice.TotalVat, invoice.CurrencyCode)));

        foreach (var group in invoice.Lines
                     .GroupBy(line => (line.VatCategory, line.VatPercentage))
                     // Charged rates first and highest first, exempt last — the order an
                     // accountant reads a VAT breakdown in, and stable so that two runs over the
                     // same document produce the same bytes.
                     .OrderBy(group => group.Key.VatCategory)
                     .ThenByDescending(group => group.Key.VatPercentage))
        {
            var category = new XElement(Cac + "TaxCategory",
                new XElement(Cbc + "ID", CategoryCode(group.Key.VatCategory)),
                new XElement(Cbc + "Percent", Number(group.Key.VatPercentage)));

            if (group.Key.VatCategory == SefVatCategory.Exempt)
            {
                // UBL allows one TaxExemptionReason per category, and taking only the first
                // line's reason silently declares every other exempt line under a legal basis it
                // does not have — so all distinct reasons are stated together.
                var reasons = group
                    .Select(line => line.VatExemptionReason?.Trim())
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct()
                    .ToList();

                if (reasons.Count > 0)
                    category.Add(new XElement(Cbc + "TaxExemptionReason", string.Join("; ", reasons)));
            }

            category.Add(new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")));

            taxTotal.Add(new XElement(Cac + "TaxSubtotal",
                new XElement(Cbc + "TaxableAmount", Amount(group.Sum(line => line.Net), invoice.CurrencyCode)),
                new XElement(Cbc + "TaxAmount", Amount(group.Sum(line => line.Vat), invoice.CurrencyCode)),
                category));
        }

        return taxTotal;
    }

    /// <summary>
    /// The second TaxTotal: the same VAT, said in the tax currency.
    ///
    /// A Serbian foreign-currency invoice is rejected without it — the tax has to be stated in the
    /// tax currency however the document is priced. It carries a TaxAmount and nothing else, which
    /// is what the CIUS asks for: the breakdown by rate belongs to the document currency's total
    /// above, and repeating it here would be two answers to one question.
    /// </summary>
    private static XElement TaxTotalInTaxCurrency(SefTaxConversion conversion, SefUblOptions options) =>
        new(Cac + "TaxTotal",
            new XElement(Cbc + "TaxAmount",
                Amount(conversion.VatInTaxCurrency, options.TaxCurrencyCode)));

    /// <summary>
    /// The rate the tax was converted at, as UBL states it: from the document's currency to the
    /// tax currency, with the date it was published for.
    /// </summary>
    private static XElement TaxExchangeRate(
        SefInvoice invoice, SefTaxConversion conversion, SefUblOptions options) =>
        new(Cac + "TaxExchangeRate",
            new XElement(Cbc + "SourceCurrencyCode", invoice.CurrencyCode),
            new XElement(Cbc + "TargetCurrencyCode", options.TaxCurrencyCode),
            // `Exact`, not `Number`: a rate is published to four decimals and the money formatter
            // caps at two. Filing 117.2043 as 117.20 understates every conversion on the document
            // by a fraction that grows with the invoice — and the stated rate would no longer
            // reproduce the stated tax, which is the pair SEF reconciles.
            //
            // Divided by the unit, because UBL states the rate per single unit of the source
            // currency and a rate quoted per 100 — JPY, HUF — would otherwise overstate the
            // conversion a hundredfold.
            new XElement(Cbc + "CalculationRate", Exact(conversion.Rate / conversion.RateUnit)),
            new XElement(Cbc + "Date", Iso(conversion.RateDate)));

    private static XElement MonetaryTotal(SefInvoice invoice) =>
        new(Cac + "LegalMonetaryTotal",
            new XElement(Cbc + "LineExtensionAmount", Amount(invoice.TotalNet, invoice.CurrencyCode)),
            new XElement(Cbc + "TaxExclusiveAmount", Amount(invoice.TotalNet, invoice.CurrencyCode)),
            new XElement(Cbc + "TaxInclusiveAmount", Amount(invoice.TotalGross, invoice.CurrencyCode)),

            // The prepayment, where there is one. UBL puts it here and takes it off the payable
            // amount below — the supply is still declared in full, which is the point: the tax on
            // it was already paid on the advance, and this states both halves rather than a net
            // figure that would understate the turnover.
            invoice.PrepaidAmount > 0m
                ? new XElement(Cbc + "PrepaidAmount",
                    Amount(invoice.PrepaidAmount, invoice.CurrencyCode))
                : null,

            new XElement(Cbc + "PayableAmount",
                Amount(invoice.TotalGross - invoice.PrepaidAmount, invoice.CurrencyCode)));

    private static XElement Line(SefInvoiceLine line, int number, string currency, bool isCreditNote)
    {
        var quantityElement = isCreditNote ? "CreditedQuantity" : "InvoicedQuantity";

        var category = new XElement(Cac + "ClassifiedTaxCategory",
            new XElement(Cbc + "ID", CategoryCode(line.VatCategory)),
            new XElement(Cbc + "Percent", Number(line.VatPercentage)),
            new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")));

        // The price the document states is the net unit price — after the line discount. Stating
        // the pre-discount price with no allowance element makes the line internally inconsistent
        // the moment a discount exists: quantity × stated price no longer gives the line amount,
        // which is exactly the arithmetic a tax inspector re-runs. The multiplication is done in
        // full precision so quantity × stated price still rounds to the stated line amount.
        var netUnitPrice = line.UnitPrice * (1m - line.DiscountPercent / 100m);

        return new XElement(Cac + (isCreditNote ? "CreditNoteLine" : "InvoiceLine"),
            new XElement(Cbc + "ID", number),
            new XElement(Cbc + quantityElement,
                new XAttribute("unitCode", UblUnits.Code(line.Unit)!), Exact(line.Quantity)),
            new XElement(Cbc + "LineExtensionAmount", Amount(line.Net, currency)),
            new XElement(Cac + "Item",
                new XElement(Cbc + "Name", line.Description),
                category),
            new XElement(Cac + "Price",
                new XElement(Cbc + "PriceAmount",
                    new XAttribute("currencyID", currency), Exact(netUnitPrice))));
    }

    private static XElement Party(XName wrapper, SefParty details)
    {
        var party = new XElement(Cac + "Party");

        if (!string.IsNullOrWhiteSpace(details.VatNumber))
        {
            party.Add(new XElement(Cac + "PartyIdentification",
                new XElement(Cbc + "ID", details.VatNumber)));
        }

        party.Add(new XElement(Cac + "PartyName", new XElement(Cbc + "Name", details.Name)));

        var postalAddress = new XElement(Cac + "PostalAddress");

        if (!string.IsNullOrWhiteSpace(details.Address))
            postalAddress.Add(new XElement(Cbc + "StreetName", details.Address));

        if (!string.IsNullOrWhiteSpace(details.City))
            postalAddress.Add(new XElement(Cbc + "CityName", details.City));

        if (!string.IsNullOrWhiteSpace(details.ZipCode))
            postalAddress.Add(new XElement(Cbc + "PostalZone", details.ZipCode));

        postalAddress.Add(new XElement(Cac + "Country",
            new XElement(Cbc + "IdentificationCode", details.CountryCode)));

        party.Add(postalAddress);

        if (!string.IsNullOrWhiteSpace(details.VatNumber))
        {
            // The tax identifier is the number prefixed with the party's OWN country code; the
            // bare number goes in PartyIdentification above. Sending the same form in both is a
            // common rejection — and prefixing a foreign buyer's identifier with the seller's
            // country files a Serbian PIB that belongs to somebody else entirely.
            party.Add(new XElement(Cac + "PartyTaxScheme",
                new XElement(Cbc + "CompanyID", $"{details.CountryCode}{details.VatNumber}"),
                new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT"))));
        }

        var legalEntity = new XElement(Cac + "PartyLegalEntity",
            new XElement(Cbc + "RegistrationName", details.Name));

        if (!string.IsNullOrWhiteSpace(details.RegistrationNumber))
            legalEntity.Add(new XElement(Cbc + "CompanyID", details.RegistrationNumber));

        party.Add(legalEntity);

        return new XElement(wrapper, party);
    }

    /// <summary>
    /// UN/CEFACT tax category codes. S is the standard rate, Z zero-rated, E exempt. Reduced rates
    /// also use S — the rate is carried by Percent, not by the category.
    /// </summary>
    private static string CategoryCode(SefVatCategory category) => category switch
    {
        SefVatCategory.Standard => "S",
        SefVatCategory.Zero => "Z",
        SefVatCategory.Exempt => "E",

        // Deliberately not a default of "S". A category added to the enum and not coded here would
        // be declared to the tax authority as standard-rated — a wrong declaration, on a document
        // that cannot be un-sent.
        _ => throw new ArgumentOutOfRangeException(
            nameof(category), category, "No UBL tax category for this VAT category.")
    };

    private static object[] Amount(decimal value, string currency) =>
        [new XAttribute("currencyID", currency), Number(value)];

    /// <summary>
    /// Invariant culture with a dot separator. A Serbian decimal comma would be rejected: the
    /// document format is fixed by the standard, not by the locale it is issued in.
    ///
    /// Only for monetary amounts, which arrive already rounded to two decimals — for those the
    /// fixed two-decimal format is lossless.
    /// </summary>
    private static string Number(decimal value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// For quantities, unit prices and exchange rates, which the standard does not cap at two
    /// decimals and which are not pre-rounded. Formatting them with "0.00" silently rounds the
    /// value: 12.345 kg declared as 12.35 kg is a quantity of goods that was never delivered, on a
    /// document that cannot be withdrawn — and the rounded factors no longer multiply to the
    /// stated line amount. Two decimals minimum, then as many as the value actually has.
    /// </summary>
    private static string Exact(decimal value) =>
        value.ToString("0.00############################", CultureInfo.InvariantCulture);

    private static string Iso(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
