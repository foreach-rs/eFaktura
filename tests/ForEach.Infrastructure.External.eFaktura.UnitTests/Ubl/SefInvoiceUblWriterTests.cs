using System.Xml.Linq;
using ForEach.Infrastructure.External.eFaktura.Ubl;

namespace ForEach.Infrastructure.External.eFaktura.UnitTests.Ubl;

/// <summary>
/// The one piece of code in this library whose output goes to a tax authority.
///
/// A wrong element here is not a failed test but an invoice the state accepts and records
/// differently from what the customer was charged — recoverable only by a storno and an
/// explanation. So the rules worth writing down are the ones where a plausible implementation
/// produces a document that is accepted and wrong: the wrong tax category, the wrong unit, a rate
/// quoted per hundred filed as if it were per one, an exemption claimed on no stated basis.
///
/// The byte-for-byte proof that the extraction from HRC changed nothing lives in HRC's golden
/// tests, which run its mapper into this writer and compare against files committed beforehand.
/// </summary>
public class SefInvoiceUblWriterTests
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    // ---- what it refuses to write --------------------------------------------------------------

    [Fact]
    public void A_debit_note_is_an_invoice_carrying_type_code_383()
    {
        // UBL has no DebitNote root. What a knjižno zaduženje does is charge more, which is what
        // an invoice does; the type code is what says it corrects an earlier document rather than
        // declaring a new supply. Filed as a plain 380 it would be accepted and recorded as a
        // second supply.
        var written = Written(Invoice() with
        {
            Type = SefInvoiceType.DebitNote,
            CorrectsInvoiceNumber = "F-0001/2026"
        });

        Assert.Equal("Invoice", written.Root!.Name.LocalName);
        Assert.Equal("383", written.Root!.Element(Cbc + "InvoiceTypeCode")?.Value);
    }

    [Fact]
    public void A_debit_note_names_the_document_it_corrects()
    {
        // Without it the authority holds a 383 correcting nothing it can identify, which reads as
        // a supply declared twice.
        var written = Written(Invoice() with
        {
            Type = SefInvoiceType.DebitNote,
            CorrectsInvoiceNumber = "F-0001/2026"
        });

        Assert.Equal("F-0001/2026", written.Root!
            .Element(Cac + "BillingReference")?
            .Element(Cac + "InvoiceDocumentReference")?
            .Element(Cbc + "ID")?.Value);
    }

    [Fact]
    public void A_correction_that_names_nothing_is_refused()
    {
        foreach (var type in new[] { SefInvoiceType.CreditNote, SefInvoiceType.DebitNote })
        {
            var result = Write(Invoice() with { Type = type, CorrectsInvoiceNumber = null });

            Assert.False(result.IsSuccess);
            Assert.Equal(SefFailureKind.CallerMistake, result.Error!.Kind);
        }
    }

    [Fact]
    public void An_advance_invoice_carries_type_code_386()
    {
        // A prepayment invoice: a declaration on money received rather than on goods delivered.
        var written = Written(Invoice() with { Type = SefInvoiceType.AdvanceInvoice });

        Assert.Equal("386", written.Root!.Element(Cbc + "InvoiceTypeCode")?.Value);
    }

    [Fact]
    public void A_final_invoice_declares_the_whole_supply_and_deducts_the_advance()
    {
        // Both halves, which is the point. The supply is declared in full because it happened in
        // full; the tax on part of it was already paid on the advance, and the prepayment is what
        // says so. A net figure would understate the turnover and lose the link.
        var written = Written(Invoice() with
        {
            Type = SefInvoiceType.FinalInvoice,
            PrepaidInvoiceNumbers = ["AV-0001/2026"],
            PrepaidAmount = 1_200m
        });

        var total = written.Root!.Element(Cac + "LegalMonetaryTotal")!;

        Assert.Equal("380", written.Root!.Element(Cbc + "InvoiceTypeCode")?.Value);
        Assert.Equal("3000.00", total.Element(Cbc + "TaxInclusiveAmount")?.Value);
        Assert.Equal("1200.00", total.Element(Cbc + "PrepaidAmount")?.Value);
        Assert.Equal("1800.00", total.Element(Cbc + "PayableAmount")?.Value);
    }

    [Fact]
    public void Each_advance_is_named_separately()
    {
        // A supply is routinely prepaid in instalments and each advance was declared as its own
        // document. One combined reference would deduct a sum against a document that never
        // carried it.
        var written = Written(Invoice() with
        {
            Type = SefInvoiceType.FinalInvoice,
            PrepaidInvoiceNumbers = ["AV-0001/2026", "AV-0002/2026"],
            PrepaidAmount = 1_200m
        });

        Assert.Equal(2, written.Root!.Elements(Cac + "BillingReference").Count());
    }

    [Fact]
    public void An_ordinary_invoice_carries_no_prepayment_element_at_all()
    {
        // Emitting a zero would assert a deduction of nothing, which is a different claim from
        // making none.
        var total = Written(Invoice()).Root!.Element(Cac + "LegalMonetaryTotal")!;

        Assert.Null(total.Element(Cbc + "PrepaidAmount"));
        Assert.Equal("3000.00", total.Element(Cbc + "PayableAmount")?.Value);
    }

    [Fact]
    public void A_deduction_naming_no_advance_is_refused()
    {
        // The tax on those advances was declared under numbers the authority holds. Deducting a
        // sum without naming them asks it to give back tax it cannot match to anything received.
        var result = Write(Invoice() with
        {
            Type = SefInvoiceType.FinalInvoice,
            PrepaidAmount = 1_200m
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(SefFailureKind.CallerMistake, result.Error!.Kind);
    }

    [Fact]
    public void Advances_named_with_nothing_deducted_are_refused()
    {
        // The reverse: the document would claim a deduction its payable amount does not make.
        Assert.False(Write(Invoice() with
        {
            Type = SefInvoiceType.FinalInvoice,
            PrepaidInvoiceNumbers = ["AV-0001/2026"],
            PrepaidAmount = 0m
        }).IsSuccess);
    }

    [Fact]
    public void Deducting_more_than_the_invoice_charges_is_refused()
    {
        // The payable amount would go negative, which is not an invoice — it is a refund, and a
        // refund is a credit note.
        Assert.False(Write(Invoice() with
        {
            Type = SefInvoiceType.FinalInvoice,
            PrepaidInvoiceNumbers = ["AV-0001/2026"],
            PrepaidAmount = 5_000m
        }).IsSuccess);
    }

    [Fact]
    public void A_prepayment_on_anything_but_a_final_invoice_is_refused()
    {
        // On an advance it would deduct the advance from itself; on a credit note it means
        // nothing.
        Assert.False(Write(Invoice() with
        {
            Type = SefInvoiceType.AdvanceInvoice,
            PrepaidInvoiceNumbers = ["AV-0001/2026"],
            PrepaidAmount = 10m
        }).IsSuccess);
    }

    [Fact]
    public void A_document_with_no_number_is_refused()
    {
        // cbc:ID is how the authority knows this document from every other one. A document filed
        // with an empty identifier can never be referenced, credited or stornoed.
        Assert.False(Write(Invoice() with { Number = "  " }).IsSuccess);
    }

    [Fact]
    public void A_credit_note_that_names_no_original_is_refused()
    {
        // It reduces "some invoice", which is not a statement a tax inspector accepts.
        var result = Write(Invoice() with
        {
            Type = SefInvoiceType.CreditNote,
            DueDate = null,
            CorrectsInvoiceNumber = null
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("ispravlja", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unit_with_no_UN_ECE_code_is_refused_rather_than_called_a_piece()
    {
        // The failure mode this exists for: "paleta" filed as H87 declares five hundred pallets as
        // five hundred pieces. Arithmetically silent, visibly wrong to anyone reading the
        // document, and impossible to withdraw.
        var result = Write(Invoice(Line() with { Unit = "paleta" }));

        Assert.False(result.IsSuccess);
        Assert.Contains("paleta", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("kom", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exempt_line_with_no_stated_basis_is_refused_here_rather_than_at_SEF()
    {
        // SEF refuses it too, with a schema message about a missing element. Refusing here says
        // which line and what is missing, to somebody who can still fix the invoice.
        var result = Write(Invoice(Line() with
        {
            VatCategory = SefVatCategory.Exempt,
            VatPercentage = 0m,
            Vat = 0m,
            VatExemptionReason = null
        }, vat: 0m));

        Assert.False(result.IsSuccess);
        Assert.Contains("osnova oslobođenja", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Totals_that_do_not_match_the_lines_they_are_made_of_are_refused()
    {
        // This is the arithmetic a tax inspector re-runs, and the one mismatch a host can produce
        // without noticing: a line edited after the totals were computed. Both figures appear in
        // the message, because "they disagree" is not something anybody can act on.
        var result = Write(Invoice() with { TotalNet = 2400m, TotalGross = 2900m });

        Assert.False(result.IsSuccess);
        Assert.Contains("2500.00", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("2400.00", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gross_that_is_not_net_plus_VAT_is_refused()
    {
        Assert.False(Write(Invoice() with { TotalGross = 3100m }).IsSuccess);
    }

    [Fact]
    public void A_conversion_on_a_document_already_in_dinars_is_refused()
    {
        // The two TaxTotals would then state the same tax twice in the same currency, which reads
        // as a document declaring double the VAT it charged.
        var result = Write(Invoice() with
        {
            Conversion = new SefTaxConversion(500m, 117.2043m, 1m, new DateOnly(2026, 7, 28))
        });

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        // Whoever fixes this is looking at one invoice. Telling them about one missing field at a
        // time turns one correction into four, each with its own round trip.
        var result = Write(Invoice(Line() with { Unit = "paleta" }) with { Number = "" });

        Assert.Equal(2, result.Error!.Details.Count);
    }

    // ---- what it writes ------------------------------------------------------------------------

    [Fact]
    public void A_rate_quoted_per_hundred_units_is_filed_per_one()
    {
        // The published HUF rate is per 100. Filing the raw figure overstates the conversion a
        // hundredfold, and the stated rate then no longer reproduces the stated tax — which is the
        // pair SEF reconciles.
        var document = Written(Invoice() with
        {
            CurrencyCode = "HUF",
            Conversion = new SefTaxConversion(1500m, 30.1234m, 100m, new DateOnly(2026, 7, 28))
        });

        Assert.Equal("0.301234", document.Descendants(Cbc + "CalculationRate").Single().Value);
    }

    [Fact]
    public void The_tax_currency_is_stated_only_when_the_document_is_in_another_one()
    {
        Assert.Empty(Written(Invoice()).Descendants(Cbc + "TaxCurrencyCode"));

        var foreign = Written(Invoice() with
        {
            CurrencyCode = "EUR",
            Conversion = new SefTaxConversion(58602m, 117.2043m, 1m, new DateOnly(2026, 7, 28))
        });

        Assert.Equal("RSD", foreign.Descendants(Cbc + "TaxCurrencyCode").Single().Value);

        // Two TaxTotals: the document's own, and the tax stated in dinars. The second carries a
        // TaxAmount and nothing else — the breakdown by rate belongs to the first.
        var totals = foreign.Descendants(Cac + "TaxTotal").ToList();

        Assert.Equal(2, totals.Count);
        Assert.Empty(totals[1].Descendants(Cac + "TaxSubtotal"));
        Assert.Equal("58602.00", totals[1].Descendants(Cbc + "TaxAmount").Single().Value);
    }

    [Fact]
    public void A_partys_tax_identifier_carries_its_own_country()
    {
        // Prefixing a German buyer's identifier with RS files a Serbian PIB that belongs to
        // somebody else entirely — a real company, named on a document it has never seen.
        var document = Written(Invoice() with
        {
            Buyer = Buyer with { CountryCode = "DE", VatNumber = "123456789" }
        });

        var customer = document.Descendants(Cac + "AccountingCustomerParty").Single();

        Assert.Equal("DE123456789", customer.Descendants(Cbc + "CompanyID").First().Value);

        // And the bare number stays in PartyIdentification: sending the same form in both is a
        // common rejection.
        Assert.Equal("123456789",
            customer.Descendants(Cac + "PartyIdentification").Single().Value);
    }

    [Fact]
    public void A_reduced_rate_is_still_the_standard_category()
    {
        // What makes it reduced is the percentage beside it. A category of its own would be a
        // different declaration about the same supply.
        var document = Written(Invoice(Line() with { VatPercentage = 10m, Vat = 250m }, vat: 250m));

        var category = document.Descendants(Cac + "ClassifiedTaxCategory").Single();

        Assert.Equal("S", category.Element(Cbc + "ID")!.Value);
        Assert.Equal("10.00", category.Element(Cbc + "Percent")!.Value);
    }

    [Fact]
    public void Every_distinct_exemption_reason_is_stated_and_not_just_the_first()
    {
        // UBL allows one TaxExemptionReason per category. Taking the first line's reason silently
        // declares every other exempt line under a legal basis it does not have.
        var document = Written(Invoice(
            [
                Exempt("PDV-RS-33"),
                Exempt("PDV-RS-24") with { Description = "Druga stavka" },
                Exempt("PDV-RS-33") with { Description = "Treća stavka" }
            ],
            net: 7500m, vat: 0m));

        var reason = document.Descendants(Cbc + "TaxExemptionReason").Single().Value;

        Assert.Equal("PDV-RS-33; PDV-RS-24", reason);
    }

    [Fact]
    public void The_stated_unit_price_is_the_one_after_the_line_discount()
    {
        // Otherwise quantity × stated price no longer gives the stated line amount, and that
        // multiplication is exactly what a reader re-runs. Full precision, so it still rounds back.
        var document = Written(Invoice(Line() with
        {
            Quantity = 1000m, UnitPrice = 5m, DiscountPercent = 50m
        }));

        Assert.Equal("2.50", document.Descendants(Cbc + "PriceAmount").Single().Value);
    }

    [Fact]
    public void A_quantity_keeps_the_decimals_it_has()
    {
        // 12.345 kg written as "12.35" declares a quantity of goods that was never delivered, on a
        // document that cannot be withdrawn.
        var document = Written(Invoice(Line() with { Unit = "kg", Quantity = 12.345m }));

        var quantity = document.Descendants(Cbc + "InvoicedQuantity").Single();

        Assert.Equal("12.345", quantity.Value);
        Assert.Equal("KGM", quantity.Attribute("unitCode")!.Value);
    }

    [Fact]
    public void A_credit_note_is_a_different_document_and_not_an_invoice_with_a_flag()
    {
        var document = Written(Invoice() with
        {
            Type = SefInvoiceType.CreditNote,
            DueDate = null,
            CorrectsInvoiceNumber = "0001/2026"
        });

        Assert.Equal("CreditNote", document.Root!.Name.LocalName);
        Assert.Equal("381", document.Descendants(Cbc + "CreditNoteTypeCode").Single().Value);

        // Quantities are credited, not invoiced, and there is no payment deadline: a credit note
        // reduces what the original asked for rather than asking for anything itself.
        Assert.Single(document.Descendants(Cbc + "CreditedQuantity"));
        Assert.Empty(document.Descendants(Cbc + "DueDate"));

        Assert.Equal("0001/2026",
            document.Descendants(Cac + "BillingReference").Single()
                .Descendants(Cbc + "ID").Single().Value);
    }

    [Fact]
    public void The_supply_date_is_stated_even_when_it_is_not_the_issue_date()
    {
        // Datum prometa decides which tax period the supply falls in, so an invoice written in
        // August for a July delivery belongs to July.
        var document = Written(Invoice() with
        {
            IssueDate = new DateOnly(2026, 8, 3),
            TaxPointDate = new DateOnly(2026, 7, 31)
        });

        var period = document.Descendants(Cac + "InvoicePeriod").Single();

        Assert.Equal("2026-07-31", period.Element(Cbc + "StartDate")!.Value);
        Assert.Equal("2026-07-31", period.Element(Cbc + "EndDate")!.Value);
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static SefResult<string> Write(SefInvoice invoice) =>
        SefInvoiceUblWriter.Write(invoice, new SefUblOptions("urn:test"));

    private static XDocument Written(SefInvoice invoice)
    {
        var result = Write(invoice);

        Assert.True(result.IsSuccess, result.FailureMessage);

        return XDocument.Parse(result.Value!);
    }

    private static SefInvoiceLine Line() => new(
        "Kesica šećera", "kom", 1000m, 2.50m, 0m,
        SefVatCategory.Standard, 20m, null, 2500m, 500m);

    private static SefInvoiceLine Exempt(string reason) => new(
        "Kesica šećera", "kom", 1000m, 2.50m, 0m,
        SefVatCategory.Exempt, 0m, reason, 2500m, 0m);

    private static readonly SefParty Seller = new(
        "HRC d.o.o.", "111111111", "20000001", "Industrijska 5", "Beograd", "11000", "RS");

    private static readonly SefParty Buyer = new(
        "Kafić Zvezda", "100000001", "20000002", "Knez Mihailova 1", "Beograd", "11000", "RS");

    private static SefInvoice Invoice(SefInvoiceLine? line = null, decimal vat = 500m) =>
        Invoice([line ?? Line()], 2500m, vat);

    private static SefInvoice Invoice(
        IReadOnlyList<SefInvoiceLine> lines, decimal net, decimal vat) => new(
        "0002/2026", SefInvoiceType.Invoice,
        new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 28), new DateOnly(2026, 7, 28),
        "RSD", Seller, Buyer, lines, net, vat, net + vat,
        BankAccount: "160-0000000000000-00");
}
