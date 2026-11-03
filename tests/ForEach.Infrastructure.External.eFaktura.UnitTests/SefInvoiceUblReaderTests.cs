using ForEach.Infrastructure.External.eFaktura.Purchase;

namespace ForEach.Infrastructure.External.eFaktura.UnitTests;

/// <summary>
/// Reading an invoice somebody else's software wrote.
///
/// The writer's tests assert what we produce; these assert what we survive. The inputs are not ours
/// to fix, so the failures worth naming are the quiet ones: an amount parsed under the wrong
/// culture, a credit note that announces itself the other way round, a due date looked for where
/// UBL does not put it — each of which produces a row that looks perfectly ordinary and is wrong.
/// </summary>
public class SefInvoiceUblReaderTests
{
    [Fact]
    public void A_conformant_invoice_gives_up_its_header()
    {
        var header = SefInvoiceUblReader.Read(Invoice());

        Assert.Equal("2026-114", header.Number);
        Assert.Equal(new DateOnly(2026, 8, 20), header.IssueDate);
        Assert.Equal(new DateOnly(2026, 9, 19), header.DueDate);
        Assert.Equal("Papir Komerc d.o.o.", header.SupplierName);
        Assert.Equal("101234567", header.SupplierVatNumber);
        Assert.Equal("20123456", header.SupplierRegistrationNumber);
        Assert.Equal("RSD", header.Currency);
        Assert.False(header.IsCreditNote);
    }

    [Fact]
    public void Amounts_are_read_with_a_point_whatever_the_server_thinks()
    {
        // The quiet one. UBL writes "125000.00"; parsed under a Serbian culture that is a hundred
        // and twenty-five million, and the inbox shows an invoice a thousand times too large — on
        // a screen whose whole job is to let somebody recognise what arrived.
        var header = SefInvoiceUblReader.Read(Invoice());

        Assert.Equal(125_000.00m, header.TotalWithoutVat);
        Assert.Equal(25_000.00m, header.TotalVat);
        Assert.Equal(150_000.00m, header.TotalWithVat);
    }

    [Fact]
    public void The_due_date_is_read_from_the_payment_terms_where_UBL_puts_it()
    {
        // There is no DueDate element in UBL. A reader looking for one finds nothing on every
        // conformant invoice ever sent, and the ageing then treats each of them as due today.
        var withoutTerms = SefInvoiceUblReader.Read(Invoice(paymentMeans: false));

        Assert.Null(withoutTerms.DueDate);
        Assert.Equal(new DateOnly(2026, 9, 19), SefInvoiceUblReader.Read(Invoice()).DueDate);
    }

    [Fact]
    public void A_credit_note_is_recognised_by_its_root_element()
    {
        // A document built to the credit-note schema. Read as an invoice it would be paid, when it
        // is the supplier reducing what we owe.
        var header = SefInvoiceUblReader.Read(CreditNoteByRoot());

        Assert.True(header.IsCreditNote);
    }

    [Fact]
    public void A_credit_note_is_recognised_by_its_type_code_too()
    {
        // The other half suppliers use: the invoice schema with type code 381. Reading only the
        // root element misses these entirely.
        var header = SefInvoiceUblReader.Read(Invoice(typeCode: "381"));

        Assert.True(header.IsCreditNote);
    }

    [Fact]
    public void The_registered_name_wins_over_the_trading_one()
    {
        // The register name is what somebody matches against the contact list. PartyName is
        // whatever the supplier calls itself on its letterhead, which may be neither.
        var header = SefInvoiceUblReader.Read(Invoice());

        Assert.Equal("Papir Komerc d.o.o.", header.SupplierName);
    }

    [Fact]
    public void An_unlabelled_tax_number_is_still_read()
    {
        // Serbian invoices carry exactly one tax scheme, and not every sender labels it. Blanking
        // the PIB would blank the field somebody uses to recognise who sent the invoice.
        var header = SefInvoiceUblReader.Read(Invoice(labelScheme: false));

        Assert.Equal("101234567", header.SupplierVatNumber);
    }

    [Fact]
    public void Something_that_is_not_XML_gives_an_empty_header_rather_than_throwing()
    {
        // The caller is a polling job storing what arrived. One unreadable invoice must not stop
        // it from storing the other forty — and the invoice exists on SEF either way, with the
        // clock on accepting it already running.
        var header = SefInvoiceUblReader.Read("<<not xml");

        Assert.Null(header.Number);
        Assert.Null(header.TotalWithVat);
        Assert.False(header.IsCreditNote);
    }

    [Fact]
    public void Nothing_at_all_gives_an_empty_header()
    {
        Assert.Null(SefInvoiceUblReader.Read(null).Number);
        Assert.Null(SefInvoiceUblReader.Read("   ").Number);
    }

    [Fact]
    public void A_document_missing_the_fields_we_want_still_produces_a_row()
    {
        // A supplier who omits what we consider essential has still sent us an invoice. A row
        // showing blanks is worth more than no row: the thing is on SEF and has to be answered.
        var header = SefInvoiceUblReader.Read(
            """<Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"/>""");

        Assert.Null(header.Number);
        Assert.Null(header.SupplierName);
        Assert.False(header.IsCreditNote);
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static string Invoice(
        string typeCode = "380", bool paymentMeans = true, bool labelScheme = true)
    {
        var terms = paymentMeans
            ? """<cac:PaymentMeans><cbc:PaymentDueDate>2026-09-19</cbc:PaymentDueDate></cac:PaymentMeans>"""
            : "";

        var scheme = labelScheme
            ? """<cbc:CompanyID schemeID="VA">101234567</cbc:CompanyID>"""
            : """<cbc:CompanyID>101234567</cbc:CompanyID>""";

        return $"""
            <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"
                     xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"
                     xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
              <cbc:ID>2026-114</cbc:ID>
              <cbc:IssueDate>2026-08-20</cbc:IssueDate>
              <cbc:InvoiceTypeCode>{typeCode}</cbc:InvoiceTypeCode>
              <cbc:DocumentCurrencyCode>RSD</cbc:DocumentCurrencyCode>
              {terms}
              <cac:AccountingSupplierParty>
                <cac:Party>
                  <cac:PartyName><cbc:Name>Papir Komerc</cbc:Name></cac:PartyName>
                  <cac:PartyTaxScheme>
                    {scheme}
                    <cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme>
                  </cac:PartyTaxScheme>
                  <cac:PartyLegalEntity>
                    <cbc:RegistrationName>Papir Komerc d.o.o.</cbc:RegistrationName>
                    <cbc:CompanyID>20123456</cbc:CompanyID>
                  </cac:PartyLegalEntity>
                </cac:Party>
              </cac:AccountingSupplierParty>
              <cac:AccountingCustomerParty>
                <cac:Party>
                  <cac:PartyTaxScheme>
                    <cbc:CompanyID schemeID="VA">109876543</cbc:CompanyID>
                    <cac:TaxScheme><cbc:ID>VAT</cbc:ID></cac:TaxScheme>
                  </cac:PartyTaxScheme>
                </cac:Party>
              </cac:AccountingCustomerParty>
              <cac:TaxTotal><cbc:TaxAmount currencyID="RSD">25000.00</cbc:TaxAmount></cac:TaxTotal>
              <cac:LegalMonetaryTotal>
                <cbc:TaxExclusiveAmount currencyID="RSD">125000.00</cbc:TaxExclusiveAmount>
                <cbc:TaxInclusiveAmount currencyID="RSD">150000.00</cbc:TaxInclusiveAmount>
              </cac:LegalMonetaryTotal>
            </Invoice>
            """;
    }

    private static string CreditNoteByRoot() =>
        """
        <CreditNote xmlns="urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2"
                    xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
          <cbc:ID>KO-3</cbc:ID>
        </CreditNote>
        """;
}
