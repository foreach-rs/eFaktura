using System.Net;
using ForEach.Infrastructure.External.eFaktura.Models;

namespace ForEach.Infrastructure.External.eFaktura.UnitTests;

/// <summary>
/// What actually goes on the wire to the tax authority: the URL and the key.
///
/// `SefClient` had no test of any kind. It was verified by hand once, against the sandbox — 200
/// with a real key, 401 with a deliberately wrong one — which proved the path and the header name
/// on the day somebody ran it and proves nothing about the code since.
///
/// Nothing here was blocked. The client takes an HttpClient, an IOptions and an ILogger, all three
/// of which a test can supply; the reason it had no tests is that nobody wrote them. That is worth
/// separating from the parts of this codebase that genuinely need a database, because the two get
/// described the same way and only one of them is true.
///
/// Two decisions, both of which survived mutation:
///
/// **The header is called `ApiKey`.** Rename it and every call answers 401. That is not a silent
/// failure, but it is a failure whose cause is invisible from this side — the response says only
/// that the key was rejected, which is what a *wrong key* also says, and the first place anybody
/// looks is the key rather than the header carrying it.
///
/// **The base URL gets a trailing slash before it is joined.** This is the one that would be found
/// last. `new Uri(base, relative)` discards the final segment of the base when it has no trailing
/// slash, so a deployment configured with `https://efaktura.mfin.gov.rs/api` silently posts to
/// `https://efaktura.mfin.gov.rs/api/publicApi/...` with the `/api` eaten — a 404 that reads as the
/// tax authority having changed its API rather than as a settings file missing one character.
/// </summary>
public class SefRequestShapeTests
{
    [Fact]
    public async Task The_api_key_travels_in_a_header_called_ApiKey()
    {
        var handler = new RecordingHandler();

        await Client(handler).GetEfakturaVersionAsync();

        Assert.NotNull(handler.Last);
        Assert.True(handler.Last!.Headers.TryGetValues("ApiKey", out var values),
            "The key is not in a header called ApiKey. SEF answers 401, which is also what a wrong "
            + "key answers, so the header name is the last thing anybody would suspect.");

        Assert.Equal("tajni-kljuc", Assert.Single(values!));
    }

    [Theory]
    // Without a trailing slash the last segment is discarded by Uri's own rules, so this is the
    // pair that matters: both spellings of the same base must reach the same address.
    [InlineData("https://efakturatest.mfin.gov.rs")]
    [InlineData("https://efakturatest.mfin.gov.rs/")]
    public async Task The_base_url_reaches_the_same_place_with_or_without_its_trailing_slash(string baseUrl)
    {
        var handler = new RecordingHandler();

        await Client(handler, baseUrl).GetEfakturaVersionAsync();

        Assert.Equal(
            "https://efakturatest.mfin.gov.rs/api/publicApi/getEfakturaVersion",
            handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_base_url_with_a_path_keeps_that_path()
    {
        // The case the trailing slash exists for. A deployment behind a gateway configures a base
        // with a path segment, and without the slash Uri throws it away silently.
        var handler = new RecordingHandler();

        await Client(handler, "https://gateway.local/sef").GetEfakturaVersionAsync();

        Assert.StartsWith("https://gateway.local/sef/api/publicApi/",
            handler.Last!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_with_no_key_or_no_url_report_themselves_unconfigured()
    {
        // What stops an unconfigured deployment posting to the wrong environment, or posting at
        // all. The question moved from the client to the credentials when this became a library:
        // whether the integration is switched on is the host's idea, and whether a call could be
        // made at all is this.
        Assert.False(new SefCredentials { BaseUrl = SefCredentials.DemoBaseUrl }.IsConfigured);
        Assert.False(new SefCredentials { ApiKey = "tajni-kljuc" }.IsConfigured);

        Assert.True(
            new SefCredentials { BaseUrl = SefCredentials.DemoBaseUrl, ApiKey = "k" }.IsConfigured);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task Whether_a_failure_is_a_refusal_or_an_unknown_is_decided_by_the_answer(
        HttpStatusCode code, bool expectedUnknown)
    {
        // The judgement in this: a 4xx means SEF read the request and declined it, so retrying
        // changes nothing and the caller should say "refused". A 5xx means SEF was reached and
        // fell over inside — a storno applied just before it failed cannot be told from one that
        // never ran — so the caller must say it does not know.
        //
        // Drawn at 500 rather than by listing individual codes, because the set of 5xx a gateway
        // can produce is not something this library gets to enumerate.
        var result = await Client(new StatusHandler(code))
            .StornoSalesInvoiceAsync(4242, "Greška u dokumentu", null);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedUnknown, result.Outcome == SefOutcome.Unknown);
    }

    [Fact]
    public async Task A_request_that_never_gets_an_answer_is_unknown()
    {
        // The case that started this: a timeout reported as "SEF refused the withdrawal" is a
        // false statement about an authority, and it sends the reader to change the document
        // rather than to find out what happened to it.
        var result = await Client(new ThrowingHandler())
            .StornoSalesInvoiceAsync(4242, "Greška u dokumentu", null);

        Assert.False(result.IsSuccess);
        Assert.Equal(SefOutcome.Unknown, result.Outcome);

        // And never retried, which is the whole point: the storno may have been applied.
        Assert.False(result.IsWorthRetrying);
    }

    /// <summary>Answers everything with the given status and a short body.</summary>
    private sealed class StatusHandler(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent("{\"message\":\"nope\"}")
            });
    }

    [Fact]
    public async Task The_same_failure_on_a_read_is_an_ordinary_failure()
    {
        // The other half of the distinction, and the reason the client asks callers to declare
        // which kind of request they are making. Nothing was changed, so asking again is safe —
        // and a read wrongly marked Unknown would freeze a status poll that only needed a retry.
        var result = await Client(new ThrowingHandler()).GetSalesInvoiceAsync(4242);

        Assert.False(result.IsSuccess);
        Assert.Equal(SefOutcome.Failed, result.Outcome);
        Assert.True(result.IsWorthRetrying);
    }

    /// <summary>A connection that never completes, the way an unreachable host behaves.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route to host");
    }

    private static SefClient Client(
        HttpMessageHandler handler,
        string baseUrl = "https://demoefaktura.mfin.gov.rs",
        string apiKey = "tajni-kljuc") =>
        new(new HttpClient(handler),
            new SefCredentials { BaseUrl = baseUrl, ApiKey = apiKey });

    /// <summary>Answers everything with an empty 200 and keeps the request that was sent.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
