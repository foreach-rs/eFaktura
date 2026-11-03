using System.Net;

namespace ForEach.Infrastructure.External.eFaktura.UnitTests;

/// <summary>
/// How the client sorts the ways a call can end. A send that may have reached SEF is Unknown and
/// must never read as a retryable failure; a connection that never opened is the opposite, and
/// treating it as Unknown would forbid a resend that is perfectly safe.
/// </summary>
public class SefClientFailureTests
{
    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task A_connection_that_never_opened_is_a_retryable_failure_even_on_a_send(
        HttpRequestError error)
    {
        // Nothing was sent, so nothing can be filed; Unknown here would stop a safe resend.
        var result = await Client(Throwing(error)).StornoSalesInvoiceAsync(4242, "Greška", null);

        Assert.Equal(SefOutcome.Failed, result.Outcome);
        Assert.NotNull(result.TransportFailure);
        Assert.True(result.IsWorthRetrying);
    }

    [Fact]
    public async Task A_connection_cut_after_it_opened_is_unknown_on_a_send()
    {
        var result = await Client(Throwing(HttpRequestError.Unknown))
            .StornoSalesInvoiceAsync(4242, "Greška", null);

        Assert.Equal(SefOutcome.Unknown, result.Outcome);
        Assert.False(result.IsWorthRetrying);
    }

    [Fact]
    public async Task Any_transport_error_on_a_read_is_a_retryable_failure()
    {
        var result = await Client(Throwing(HttpRequestError.Unknown)).GetSalesInvoiceAsync(4242);

        Assert.Equal(SefOutcome.Failed, result.Outcome);
        Assert.True(result.IsWorthRetrying);
    }

    [Fact]
    public async Task A_send_that_times_out_is_unknown_and_a_read_that_times_out_is_a_retry()
    {
        var send = await Client(Hanging()).StornoSalesInvoiceAsync(4242, "Greška", null);
        var read = await Client(Hanging()).GetSalesInvoicePdfAsync(4242);

        Assert.Equal(SefOutcome.Unknown, send.Outcome);
        Assert.Equal(SefOutcome.Failed, read.Outcome);
        Assert.True(read.IsWorthRetrying);
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates_on_every_path()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(Hanging()).StornoSalesInvoiceAsync(4242, "Greška", null, cts.Token));

        // The byte-returning path used to catch TaskCanceledException and report it as Transport.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(Hanging()).GetSalesInvoicePdfAsync(4242, cts.Token));
    }

    [Fact]
    public async Task A_5xx_on_a_send_is_unknown_and_keeps_the_status_and_the_body()
    {
        var result = await Client(Answering(HttpStatusCode.BadGateway, "bad gateway"))
            .StornoSalesInvoiceAsync(4242, "Greška", null);

        Assert.Equal(SefOutcome.Unknown, result.Outcome);
        Assert.Equal(502, result.HttpStatusCode);
        Assert.Equal("bad gateway", result.ResponseBody);
    }

    [Fact]
    public async Task A_200_naming_no_invoice_id_on_a_send_is_unknown()
    {
        // The invoice may exist at SEF and cannot be recorded here: the same predicament as a
        // timeout, and reported as one.
        var result = await Client(Answering(HttpStatusCode.OK, "{}"))
            .SendSalesInvoiceUblAsync("<Invoice/>", "REQ-1", Models.SefCirMode.NotSpecified);

        Assert.Equal(SefOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task A_404_is_the_callers_mistake_and_not_worth_retrying()
    {
        // A wrong invoice id would otherwise be asked for again on every scheduled run, for ever.
        var result = await Client(Answering(HttpStatusCode.NotFound, "{}")).GetSalesInvoiceAsync(4242);

        Assert.Equal(SefOutcome.Failed, result.Outcome);
        Assert.Equal(SefFailureKind.CallerMistake, result.Error!.Kind);
        Assert.False(result.IsWorthRetrying);
    }

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("http://exa mple.com")]
    public async Task A_base_url_that_is_not_a_url_is_a_failure_and_not_an_exception(string baseUrl)
    {
        var client = new SefClient(
            new HttpClient(Answering(HttpStatusCode.OK, "{}")),
            new SefCredentials { BaseUrl = baseUrl, ApiKey = "k", TimeoutSeconds = 1 });

        var read = await client.GetSalesInvoiceAsync(4242);
        var bytes = await client.GetSalesInvoicePdfAsync(4242);

        Assert.Equal(SefOutcome.Failed, read.Outcome);
        Assert.Equal(SefOutcome.Failed, bytes.Outcome);
    }

    private static SefClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            new SefCredentials { BaseUrl = "https://sef.test", ApiKey = "k", TimeoutSeconds = 1 });

    private static Handler Throwing(HttpRequestError error) =>
        new((_, _) => throw new HttpRequestException(error, "refused", null));

    private static Handler Hanging() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    private static Handler Answering(HttpStatusCode code, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) }));

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
