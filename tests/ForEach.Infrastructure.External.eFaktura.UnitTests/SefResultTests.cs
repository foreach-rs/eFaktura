using ForEach.Infrastructure.External.eFaktura;

namespace ForEach.Infrastructure.External.eFaktura.UnitTests;

/// <summary>
/// The three ways a SEF call ends, and why the third one exists.
///
/// Filing an invoice is not idempotent: send the same document twice and the tax authority holds
/// two. So a timeout on a send is not a failure — it is an unknown, and the whole of this type
/// exists to stop a caller treating one as the other. Everything the host does about it
/// afterwards — reconciling by requestId, refusing to resend — hangs off that distinction.
/// </summary>
public class SefResultTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void A_key_problem_is_configuration(int status) =>
        Assert.Equal(SefFailureKind.Configuration, Error(status).Kind);

    [Theory]
    [InlineData(400)]   // UBL that does not validate
    [InlineData(409)]   // an action the invoice's status forbids
    [InlineData(422)]
    public void A_document_or_a_request_that_is_wrong_is_ours_to_fix(int status)
    {
        var error = Error(status);

        Assert.Equal(SefFailureKind.CallerMistake, error.Kind);

        // And never retried: the same document would be sent again, and refused again.
        Assert.False(SefResult<string>.Fail(error).IsWorthRetrying);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void A_SEF_side_problem_is_worth_waiting_out(int status)
    {
        var error = Error(status);

        Assert.Equal(SefFailureKind.Transient, error.Kind);
        Assert.True(SefResult<string>.Fail(error).IsWorthRetrying);
    }

    [Fact]
    public void A_status_nobody_has_written_a_row_for_is_retried()
    {
        // The same reasoning as the NBS and Meta libraries: a wasted attempt costs one scheduled
        // run, and giving up costs an invoice nobody filed.
        Assert.Equal(SefFailureKind.Transient, Error(418).Kind);
    }

    [Fact]
    public void An_unknown_outcome_is_never_retried()
    {
        // The rule this type exists for. The invoice may be filed; sending it again would put a
        // second one with the same number at the tax authority, and unpicking that is a storno and
        // an explanation rather than a bug fix.
        var result = SefResult<string>.Unknown("The operation was canceled.");

        Assert.Equal(SefOutcome.Unknown, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsWorthRetrying);
    }

    [Fact]
    public void A_call_that_never_reached_SEF_is_a_plain_failure()
    {
        // The other half of the same distinction: nothing was sent, so nothing was filed, and
        // trying again is both safe and the right thing to do.
        var result = SefResult<string>.Transport("No such host is known.");

        Assert.Equal(SefOutcome.Failed, result.Outcome);
        Assert.True(result.IsWorthRetrying);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SEFs_validation_messages_survive_word_for_word()
    {
        // On a rejected invoice these are the whole of the useful content: "polje BT-31 nije
        // popunjeno" tells an accountant what to fix, and a summary of it tells them nothing.
        var error = new SefError(
            400, "InvalidUbl", "Dokument nije validan.",
            ["Polje BT-31 nije popunjeno.", "Zbir stavki se ne poklapa sa ukupnim iznosom."]);

        Assert.Contains("BT-31", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("ne poklapa", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_response_body_is_kept_even_on_success()
    {
        // The host stores it. When an accountant asks in six months why an invoice was rejected —
        // or what SEF returned when it was accepted — the answer has to be what SEF actually said.
        var result = SefResult<string>.Ok("value", responseBody: """{"InvoiceId":123}""", httpStatusCode: 200);

        Assert.True(result.IsSuccess);
        Assert.Contains("InvoiceId", result.ResponseBody!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_is_part_of_the_credentials_rather_than_a_flag_beside_them()
    {
        // A demo key against production is refused with the same 401 as a revoked one, so the
        // address and the key belong together — and a host about to file a sample invoice can ask
        // whether it is about to file it with the state.
        var demo = new SefCredentials { BaseUrl = SefCredentials.DemoBaseUrl, ApiKey = "k" };
        var live = new SefCredentials { BaseUrl = SefCredentials.ProductionBaseUrl, ApiKey = "k" };

        Assert.False(demo.IsProduction);
        Assert.True(live.IsProduction);

        Assert.True(demo.IsConfigured);
        Assert.False(new SefCredentials { BaseUrl = SefCredentials.DemoBaseUrl }.IsConfigured);
    }

    [Fact]
    public void An_endpoint_address_survives_a_trailing_slash_on_either_side()
    {
        var credentials = new SefCredentials { BaseUrl = "https://demoefaktura.mfin.gov.rs/", ApiKey = "k" };

        Assert.Equal(
            "https://demoefaktura.mfin.gov.rs/api/publicApi/sales-invoice/ubl",
            credentials.Endpoint("/api/publicApi/sales-invoice/ubl").ToString());
    }

    private static SefError Error(int status) => new(status, null, "…", []);
}
