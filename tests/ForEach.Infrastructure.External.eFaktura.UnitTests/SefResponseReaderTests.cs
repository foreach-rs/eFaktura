using System.Globalization;

using ForEach.Infrastructure.External.eFaktura.Models;

namespace ForEach.Infrastructure.External.eFaktura.UnitTests;

/// <summary>
/// Reading what the tax authority said.
///
/// Every other SEF test here runs against a fake client that hands the changes over directly, so
/// this was the one part of the status path nothing exercised — and it is the part that decides
/// whether an invoice reads as Approved or Rejected on a document that is a legal record.
/// </summary>
public class SefResponseReaderTests
{
    [Fact]
    public void A_change_is_read_field_by_field()
    {
        const string body = """
            [ { "EventId": 12, "Date": "2026-07-29T10:15:00Z", "NewInvoiceStatus": "Approved",
                "SalesInvoiceId": 4001, "Comment": "OK", "CirInvoiceId": 77 } ]
            """;

        var change = Assert.Single(SefResponseReader.ReadChanges(body, "SalesInvoiceId"));

        Assert.Equal(12, change.EventId);
        Assert.Equal("Approved", change.NewInvoiceStatus);
        Assert.Equal(4001, change.InvoiceId);
        Assert.Equal("OK", change.Comment);
        Assert.Equal(77, change.CirInvoiceId);
    }

    [Fact]
    public void The_id_field_named_by_the_caller_is_the_one_read()
    {
        // Sales and purchase invoices come from the same shape under different names, so the field
        // is a parameter. Reading the wrong one gives every change an invoice id of zero, which
        // matches no invoice and updates nothing — a status poll that runs cleanly and forever
        // without ever changing anything.
        const string body = """
            [ { "EventId": 1, "SalesInvoiceId": 4001, "PurchaseInvoiceId": 9001 } ]
            """;

        Assert.Equal(4001, SefResponseReader.ReadChanges(body, "SalesInvoiceId")[0].InvoiceId);
        Assert.Equal(9001, SefResponseReader.ReadChanges(body, "PurchaseInvoiceId")[0].InvoiceId);
    }

    [Fact]
    public void A_timestamp_is_read_as_utc_whatever_the_server_runs_under()
    {
        // The culture is not a formality. TryParse without one uses the server's, and the
        // separators in an ISO string are literal so those were never the risk — the calendar was.
        // Under a culture whose default calendar is not Gregorian the same text becomes a date
        // centuries away, on the value that decides which invoice event is newer.
        var original = CultureInfo.CurrentCulture;

        try
        {
            foreach (var culture in new[] { "ar-SA", "en-US", "sr-Latn-RS" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

                const string body = """
                    [ { "EventId": 1, "Date": "2026-07-29T10:15:00Z" } ]
                    """;

                var change = Assert.Single(SefResponseReader.ReadChanges(body, "SalesInvoiceId"));

                Assert.Equal(new DateTime(2026, 7, 29, 10, 15, 0, DateTimeKind.Utc), change.Date);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_timestamp_carrying_an_offset_arrives_as_utc_rather_than_local()
    {
        // Npgsql refuses a Local-kind value for a timestamptz column, so this is the difference
        // between a stored event and a throw. An offset of +02:00 is Belgrade in summer, which is
        // the offset these actually carry.
        const string body = """
            [ { "EventId": 1, "Date": "2026-07-29T12:15:00+02:00" } ]
            """;

        var change = Assert.Single(SefResponseReader.ReadChanges(body, "SalesInvoiceId"));

        Assert.Equal(DateTimeKind.Utc, change.Date.Kind);
        Assert.Equal(new DateTime(2026, 7, 29, 10, 15, 0, DateTimeKind.Utc), change.Date);
    }

    [Fact]
    public void Several_changes_keep_the_order_they_arrived_in()
    {
        const string body = """
            [ { "EventId": 3, "SalesInvoiceId": 1 },
              { "EventId": 1, "SalesInvoiceId": 2 },
              { "EventId": 2, "SalesInvoiceId": 3 } ]
            """;

        Assert.Equal([3L, 1L, 2L],
            SefResponseReader.ReadChanges(body, "SalesInvoiceId").Select(c => c.EventId));
    }

    [Fact]
    public void A_missing_field_falls_back_rather_than_dropping_the_change()
    {
        // A change with no status is still a change: the event id has to be recorded or the poller
        // asks for it again next time. The fallbacks are what the client already did; they are
        // asserted so that "0 and MinValue" stays a decision rather than an accident.
        const string body = """
            [ { "SalesInvoiceId": 4001 } ]
            """;

        var change = Assert.Single(SefResponseReader.ReadChanges(body, "SalesInvoiceId"));

        Assert.Equal(0, change.EventId);
        Assert.Equal(DateTime.MinValue, change.Date);
        Assert.Null(change.NewInvoiceStatus);
        Assert.Null(change.CirInvoiceId);
    }

    [Fact]
    public void A_field_of_the_wrong_type_is_not_forced()
    {
        // A status arriving as a number is a different protocol, not this one, and an id arriving
        // as a string would be a silent mismatch against a long column.
        const string body = """
            [ { "EventId": "12", "NewInvoiceStatus": 3, "SalesInvoiceId": 4001 } ]
            """;

        var change = Assert.Single(SefResponseReader.ReadChanges(body, "SalesInvoiceId"));

        Assert.Equal(0, change.EventId);
        Assert.Null(change.NewInvoiceStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"message\": \"no changes\" }")]
    [InlineData("<html>502</html>")]
    public void Anything_that_is_not_an_array_of_changes_reads_as_no_changes(string body)
    {
        // Empty rather than a failure, and that is the deliberate half: "nothing has changed" is
        // the ordinary answer to this call. A poll treating it as an error would log a failure
        // every few minutes on a healthy system until somebody stopped reading the log.
        Assert.Empty(SefResponseReader.ReadChanges(body, "SalesInvoiceId"));
    }

    [Fact]
    public void An_entry_that_is_not_an_object_is_skipped_rather_than_ending_the_read()
    {
        // One malformed element must not cost the changes after it — those are the events this
        // poll exists to collect, and a dropped one is a status that never arrives.
        const string body = """
            [ 5, { "EventId": 1, "SalesInvoiceId": 4001 } ]
            """;

        var change = Assert.Single(SefResponseReader.ReadChanges(body, "SalesInvoiceId"));

        Assert.Equal(1, change.EventId);
    }

    // ---- the id SEF files an invoice under --------------------------------------------------------

    [Fact]
    public void The_first_named_id_present_on_the_root_is_returned()
    {
        // SEF spells it differently in different replies, so the caller passes both. Order matters
        // only when both are present, which is the case worth pinning.
        const string body = """
            { "InvoiceId": 4001, "invoiceId": 9001 }
            """;

        Assert.Equal(4001, SefResponseReader.ReadId(body, "InvoiceId", "invoiceId"));
        Assert.Equal(9001, SefResponseReader.ReadId(body, "invoiceId", "InvoiceId"));
    }

    [Fact]
    public void An_id_nested_below_the_root_is_not_taken()
    {
        // Taking it would file the invoice under an id the register never issued for it, and a
        // later status poll would match on that id and find nothing forever.
        const string body = """
            { "data": { "InvoiceId": 4001 } }
            """;

        Assert.Null(SefResponseReader.ReadId(body, "InvoiceId"));
    }

    [Fact]
    public void An_id_sent_as_a_string_is_not_read_as_a_number()
    {
        const string body = """
            { "InvoiceId": "4001" }
            """;

        Assert.Null(SefResponseReader.ReadId(body, "InvoiceId"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("4001")]
    [InlineData("[4001]")]
    [InlineData("<html>502</html>")]
    public void A_reply_that_is_not_an_object_yields_no_id_rather_than_throwing(string body)
    {
        Assert.Null(SefResponseReader.ReadId(body, "InvoiceId"));
    }

    // ---- the id list ------------------------------------------------------------------------------

    [Fact]
    public void An_id_list_is_read_in_order()
    {
        Assert.Equal([3L, 1L, 2L], SefResponseReader.ReadIds("[3, 1, 2]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ \"ids\": [1, 2] }")]
    [InlineData("not json")]
    public void Anything_else_reads_as_an_empty_id_list(string body)
    {
        Assert.Empty(SefResponseReader.ReadIds(body));
    }

    [Fact]
    public void A_non_numeric_entry_in_the_id_list_is_ignored_rather_than_fatal()
    {
        Assert.Equal([1L, 2L], SefResponseReader.ReadIds("""[1, "x", null, 2]"""));
    }

    [Fact]
    public void A_null_body_is_answered_rather_than_thrown_on()
    {
        // All three of these declare `string? body`, so null is part of their contract, and each
        // opens with a null-or-whitespace guard. Only the null half of that guard does any work:
        // an empty or whitespace body reaches JsonDocument.Parse, throws JsonException, and is
        // caught — so removing the guard changes nothing for those. A *null* body throws
        // ArgumentNullException instead, which the catch does not cover, and it would surface as an
        // unhandled exception inside the SEF polling job rather than as a status left unchanged.
        //
        // Not reachable from this application today: every caller passes a body read from an HTTP
        // response, and ReadAsStringAsync does not return null. Pinned because the signature says
        // otherwise — the same reasoning as the UBL currency, and the opposite of the quote-title
        // fallback, which was a defensive string rather than a declared contract.
        //
        // Worth noting beside it: EOtpremnicaResponseReader takes a non-nullable `string body`, so
        // the two readers were written to different contracts. Neither is wrong; they simply do not
        // agree, and only one of them has to answer this question.
        Assert.Empty(SefResponseReader.ReadChanges(null, "SalesInvoiceId"));
        Assert.Empty(SefResponseReader.ReadIds(null));
        Assert.Null(SefResponseReader.ReadId(null, "InvoiceId"));
    }
}
