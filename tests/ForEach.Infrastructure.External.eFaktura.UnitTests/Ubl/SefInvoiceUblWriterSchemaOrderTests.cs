using System.Xml.Linq;
using ForEach.Infrastructure.External.eFaktura.Ubl;

namespace ForEach.Infrastructure.External.eFaktura.UnitTests.Ubl;

/// <summary>
/// The writer's output against the order of children the UBL 2.1 schemas demand. A schema is a
/// sequence, so an element written in the wrong place is a document that fails validation however
/// right its content is — and SEF's answer to it is a message about the schema, not about the
/// mistake.
///
/// The sequences below are the root children of UBL-Invoice-2.1.xsd and UBL-CreditNote-2.1.xsd,
/// in schema order. A document is in order when the position of each child in its schema never goes
/// backwards.
/// </summary>
public class SefInvoiceUblWriterSchemaOrderTests
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private static readonly string[] InvoiceSequence =
    [
        "UBLVersionID", "CustomizationID", "ProfileID", "ProfileExecutionID", "ID", "CopyIndicator",
        "UUID", "IssueDate", "IssueTime", "DueDate", "InvoiceTypeCode", "Note", "TaxPointDate",
        "DocumentCurrencyCode", "TaxCurrencyCode", "PricingCurrencyCode", "PaymentCurrencyCode",
        "PaymentAlternativeCurrencyCode", "AccountingCostCode", "AccountingCost", "LineCountNumeric",
        "BuyerReference", "InvoicePeriod", "OrderReference", "BillingReference",
        "DespatchDocumentReference", "ReceiptDocumentReference", "StatementDocumentReference",
        "OriginatorDocumentReference", "ContractDocumentReference", "AdditionalDocumentReference",
        "ProjectReference", "Signature", "AccountingSupplierParty", "AccountingCustomerParty",
        "PayeeParty", "BuyerCustomerParty", "SellerSupplierParty", "TaxRepresentativeParty",
        "Delivery", "DeliveryTerms", "PaymentMeans", "PaymentTerms", "PrepaidPayment",
        "AllowanceCharge", "TaxExchangeRate", "PricingExchangeRate", "PaymentExchangeRate",
        "PaymentAlternativeExchangeRate", "TaxTotal", "WithholdingTaxTotal", "LegalMonetaryTotal",
        "InvoiceLine"
    ];

    private static readonly string[] CreditNoteSequence =
    [
        "UBLVersionID", "CustomizationID", "ProfileID", "ProfileExecutionID", "ID", "CopyIndicator",
        "UUID", "IssueDate", "IssueTime", "TaxPointDate", "CreditNoteTypeCode", "Note",
        "DocumentCurrencyCode", "TaxCurrencyCode", "PricingCurrencyCode", "PaymentCurrencyCode",
        "PaymentAlternativeCurrencyCode", "AccountingCostCode", "AccountingCost", "LineCountNumeric",
        "BuyerReference", "InvoicePeriod", "DiscrepancyResponse", "OrderReference", "BillingReference",
        "DespatchDocumentReference", "ReceiptDocumentReference", "ContractDocumentReference",
        "AdditionalDocumentReference", "StatementDocumentReference", "OriginatorDocumentReference",
        "Signature", "AccountingSupplierParty", "AccountingCustomerParty", "PayeeParty",
        "BuyerCustomerParty", "SellerSupplierParty", "TaxRepresentativeParty", "Delivery",
        "DeliveryTerms", "PaymentMeans", "PaymentTerms", "TaxExchangeRate", "PricingExchangeRate",
        "PaymentExchangeRate", "PaymentAlternativeExchangeRate", "AllowanceCharge", "TaxTotal",
        "LegalMonetaryTotal", "CreditNoteLine"
    ];

    [Fact]
    public void An_invoice_with_a_note_and_a_conversion_is_in_schema_order()
    {
        var root = Written(Foreign() with { Note = "Napomena" }).Root!;

        AssertInOrder(InvoiceSequence, root);
    }

    [Fact]
    public void A_credit_note_with_a_note_and_a_conversion_is_in_schema_order()
    {
        var root = Written(Foreign() with
        {
            Type = SefInvoiceType.CreditNote,
            DueDate = null,
            CorrectsInvoiceNumber = "0001/2026",
            Note = "Napomena"
        }).Root!;

        AssertInOrder(CreditNoteSequence, root);
    }

    [Fact]
    public void The_note_follows_the_type_code_directly()
    {
        var root = Written(Foreign() with { Note = "Napomena" }).Root!;

        var names = root.Elements().Select(e => e.Name.LocalName).ToList();

        Assert.Equal("Note", names[names.IndexOf("InvoiceTypeCode") + 1]);
    }

    [Fact]
    public void The_exchange_rate_comes_before_the_first_tax_total_and_both_totals_follow_it()
    {
        var names = Written(Foreign()).Root!.Elements().Select(e => e.Name.LocalName).ToList();

        var rate = names.IndexOf("TaxExchangeRate");

        Assert.True(rate >= 0);
        Assert.Equal(["TaxTotal", "TaxTotal"], names.Skip(rate + 1).Take(2));
    }

    [Fact]
    public void The_output_starts_with_an_xml_declaration_for_utf8()
    {
        var result = SefInvoiceUblWriter.Write(Domestic(), new SefUblOptions("urn:test"));

        Assert.True(result.IsSuccess, result.FailureMessage);
        Assert.StartsWith("<?xml", result.Value!, StringComparison.Ordinal);
        Assert.Contains("UTF-8", result.Value!.Split('\n')[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_credit_note_with_no_reference_reports_it_once()
    {
        var result = SefInvoiceUblWriter.Write(
            Domestic() with { Type = SefInvoiceType.CreditNote, DueDate = null, CorrectsInvoiceNumber = null },
            new SefUblOptions("urn:test"));

        Assert.False(result.IsSuccess);
        Assert.Single(result.Error!.Details);
    }

    private static void AssertInOrder(string[] sequence, XElement root)
    {
        var positions = root.Elements().Select(e => Array.IndexOf(sequence, e.Name.LocalName)).ToList();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order().ToList(), positions);
    }

    private static XDocument Written(SefInvoice invoice)
    {
        var result = SefInvoiceUblWriter.Write(invoice, new SefUblOptions("urn:test"));

        Assert.True(result.IsSuccess, result.FailureMessage);

        return XDocument.Parse(result.Value!);
    }

    private static readonly SefParty Seller = new(
        "HRC d.o.o.", "111111111", "20000001", "Industrijska 5", "Beograd", "11000", "RS");

    private static readonly SefParty Buyer = new(
        "Kafić Zvezda", "100000001", "20000002", "Knez Mihailova 1", "Beograd", "11000", "RS");

    private static SefInvoice Domestic() => new(
        "0002/2026", SefInvoiceType.Invoice,
        new DateOnly(2026, 7, 29), new DateOnly(2026, 8, 28), new DateOnly(2026, 7, 28),
        "RSD", Seller, Buyer,
        [new SefInvoiceLine("Kesica šećera", "kom", 1000m, 2.50m, 0m,
            SefVatCategory.Standard, 20m, null, 2500m, 500m)],
        2500m, 500m, 3000m,
        BankAccount: "160-0000000000000-00");

    private static SefInvoice Foreign() => Domestic() with
    {
        CurrencyCode = "EUR",
        Conversion = new SefTaxConversion(58602m, 117.2043m, 1m, new DateOnly(2026, 7, 28))
    };
}
