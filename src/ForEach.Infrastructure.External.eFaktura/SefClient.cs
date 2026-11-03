using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ForEach.Infrastructure.External.eFaktura.Models;

namespace ForEach.Infrastructure.External.eFaktura;

/// <summary>
/// eFaktura Public API. Paths, the ApiKey header and the request shapes below are taken from
/// the official API documentation; the surface is wide, so it is grouped by area and every
/// call funnels through one exchange method that handles auth, errors and logging once.
/// </summary>
public sealed class SefClient(HttpClient httpClient, SefCredentials credentials)
{

    private const string Sales = "api/publicApi/sales-invoice";
    private const string Purchase = "api/publicApi/purchase-invoice";
    private const string Public = "api/publicApi";
    private const string VatV2 = "api/v2/publicApi/vat-recording";


    // ---- Sales invoices -------------------------------------------------------------------

    /// <summary>
    /// One authenticated GET, for "Testiraj vezu". The id list is the cheapest call in the API
    /// that still proves the key was accepted — a reachability check would pass with a key SEF
    /// rejects, which is the failure the button exists to find.
    /// </summary>
    /// <param name="today">
    /// The host's idea of today. Passed in rather than read from the clock: which day it is in
    /// Belgrade is a business question, and a library that answered it would answer it in UTC.
    /// </param>
    /// <param name="ct">Cancels the call. A cancellation by the caller propagates; a timeout does not.</param>
    public async Task<SefResult<bool>> ProbeAsync(DateTime today, CancellationToken ct = default)
    {
        // A one-day window ending today: the narrowest question the endpoint accepts, so the
        // probe costs SEF as little as possible while still exercising the key.
        var result = await GetSalesInvoiceIdsAsync(today.AddDays(-1), today, ct);

        return result.IsSuccess
            ? SefResult<bool>.Ok(true, result.ResponseBody, result.HttpStatusCode)
            : Carry<IReadOnlyList<long>, bool>(result);
    }

    /// <summary>
    /// One failed result as another's type, with everything that made it a failure intact —
    /// including Unknown, which must never be flattened into a plain failure on the way past.
    /// </summary>
    private static SefResult<TTo> Carry<TFrom, TTo>(SefResult<TFrom> result) =>
        result.Outcome == SefOutcome.Unknown
            ? SefResult<TTo>.Unknown(
                result.FailureMessage, result.HttpStatusCode, result.ResponseBody)
            : result.Error is { } error
                ? SefResult<TTo>.Fail(error, result.ResponseBody)
                : SefResult<TTo>.Transport(result.FailureMessage);

    /// <summary>Files a UBL invoice as a multipart upload; the requestId makes a resend after a timeout findable.</summary>
    public Task<SefResult<SefInvoiceIds>> UploadSalesInvoiceUblAsync(
        string ubl, string requestId, SefCirMode sendToCir, CancellationToken ct = default)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ubl));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/xml");

        // The form field is named ublFile; anything else is ignored and the request fails
        // validation with a message that does not mention the field name.
        content.Add(file, "ublFile", $"invoice-{requestId}.xml");

        return SendForIdsAsync(
            $"{Sales}/ubl/upload{UploadQuery(requestId, sendToCir)}", content, ct);
    }

    /// <summary>Files a UBL invoice as a raw XML body; the requestId makes a resend after a timeout findable.</summary>
    public Task<SefResult<SefInvoiceIds>> SendSalesInvoiceUblAsync(
        string ubl, string requestId, SefCirMode sendToCir, CancellationToken ct = default)
    {
        var content = new StringContent(ubl, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };

        return SendForIdsAsync($"{Sales}/ubl{UploadQuery(requestId, sendToCir)}", content, ct);
    }

    private static string UploadQuery(string requestId, SefCirMode sendToCir)
    {
        var query = $"?requestId={Uri.EscapeDataString(requestId)}";

        // Omitted rather than sent as "No" when unspecified: the parameter is only meaningful
        // for public-sector buyers, and a wrong explicit value is worse than its absence.
        return sendToCir == SefCirMode.NotSpecified ? query : $"{query}&sendToCir={sendToCir}";
    }

    private async Task<SefResult<SefInvoiceIds>> SendForIdsAsync(
        string path, HttpContent content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };

        // The one kind of call where a timeout is not a failure: this is the send.
        var body = await ExchangeAsync(request, changesState: true, ct);

        if (!body.IsSuccess) return Carry<string, SefInvoiceIds>(body);

        var invoiceId = SefResponseReader.ReadId(body.Value!, "InvoiceId", "invoiceId");

        if (invoiceId is null)
        {
            // Accepted with no id is an invoice that exists at the tax authority and cannot be
            // recorded here — which is the same predicament as a timeout, and reported as one.
            return SefResult<SefInvoiceIds>.Unknown(
                "SEF accepted the document but returned no invoice identifier.");
        }

        return SefResult<SefInvoiceIds>.Ok(
            new SefInvoiceIds(
                invoiceId.Value,
                SefResponseReader.ReadId(body.Value!, "SalesInvoiceId", "salesInvoiceId"),
                SefResponseReader.ReadId(body.Value!, "PurchaseInvoiceId", "purchaseInvoiceId"),
                body.Value!),
            body.ResponseBody,
            body.HttpStatusCode);
    }

    /// <summary>One sales invoice as SEF describes it.</summary>
    public Task<SefResult<string>> GetSalesInvoiceAsync(long invoiceId, CancellationToken ct = default) =>
        GetAsync($"{Sales}?invoiceId={invoiceId}", ct);

    /// <summary>Status changes on sales invoices for the day of <paramref name="since"/>.</summary>
    public Task<SefResult<IReadOnlyList<SefStatusChange>>> GetSalesInvoiceChangesAsync(
        DateTime since, CancellationToken ct = default) =>
        ChangesAsync($"{Sales}/changes?date={Iso(since)}", "SalesInvoiceId", ct);

    /// <summary>Ids of the sales invoices changed between two dates.</summary>
    public Task<SefResult<IReadOnlyList<long>>> GetSalesInvoiceIdsAsync(
        DateTime from, DateTime to, CancellationToken ct = default) =>
        IdsAsync($"{Sales}/ids?fromDate={Iso(from)}&toDate={Iso(to)}", ct);

    /// <summary>The UBL of a sales invoice, as filed.</summary>
    public Task<SefResult<string>> GetSalesInvoiceXmlAsync(long invoiceId, CancellationToken ct = default) =>
        GetAsync($"{Sales}/xml?invoiceId={invoiceId}", ct);

    /// <summary>A sales invoice rendered as PDF.</summary>
    public Task<SefResult<byte[]>> GetSalesInvoicePdfAsync(long invoiceId, CancellationToken ct = default) =>
        GetBytesAsync($"{Sales}/pdf?invoiceId={invoiceId}", ct);

    /// <summary>The status history of a sales invoice as PDF.</summary>
    public Task<SefResult<byte[]>> GetSalesInvoiceStatusHistoryPdfAsync(
        long invoiceId, CancellationToken ct = default) =>
        GetBytesAsync($"{Sales}/status-history/{invoiceId}/pdf", ct);

    /// <summary>Withdraws a sales invoice the buyer has not yet decided on.</summary>
    public Task<SefResult<string>> CancelSalesInvoiceAsync(
        long invoiceId, string comment, CancellationToken ct = default) =>
        PostJsonAsync($"{Sales}/cancel", new { invoiceId, cancelComments = comment }, ct);

    /// <summary>Reverses a sales invoice the buyer has already accepted.</summary>
    public Task<SefResult<string>> StornoSalesInvoiceAsync(
        long invoiceId, string comment, string? stornoNumber, CancellationToken ct = default) =>
        PostJsonAsync($"{Sales}/storno",
            new { invoiceId, stornoNumber, stornoComment = comment }, ct);

    /// <summary>Deletes sales invoices that SEF holds only as drafts.</summary>
    public async Task<SefResult<string>> DeleteSalesInvoicesAsync(
        IReadOnlyList<long> invoiceIds, CancellationToken ct = default)
    {
        // A DELETE carrying a body is unusual, but it is what the API specifies.
        using var request = new HttpRequestMessage(HttpMethod.Delete, Sales)
        {
            Content = JsonBody(invoiceIds)
        };

        return await ExchangeAsync(request, changesState: true, ct);
    }

    // ---- Purchase invoices ----------------------------------------------------------------

    /// <summary>One purchase invoice as SEF describes it.</summary>
    public Task<SefResult<string>> GetPurchaseInvoiceAsync(long invoiceId, CancellationToken ct = default) =>
        GetAsync($"{Purchase}?invoiceId={invoiceId}", ct);

    /// <summary>Status changes on purchase invoices for the day of <paramref name="since"/>.</summary>
    public Task<SefResult<IReadOnlyList<SefStatusChange>>> GetPurchaseInvoiceChangesAsync(
        DateTime since, CancellationToken ct = default) =>
        ChangesAsync($"{Purchase}/changes?date={Iso(since)}", "PurchaseInvoiceId", ct);

    /// <summary>Ids of the purchase invoices changed between two dates.</summary>
    public Task<SefResult<IReadOnlyList<long>>> GetPurchaseInvoiceIdsAsync(
        DateTime from, DateTime to, CancellationToken ct = default) =>
        IdsAsync($"{Purchase}/ids?fromDate={Iso(from)}&toDate={Iso(to)}", ct);

    /// <summary>The UBL of a purchase invoice, as received.</summary>
    public Task<SefResult<string>> GetPurchaseInvoiceXmlAsync(long invoiceId, CancellationToken ct = default) =>
        GetAsync($"{Purchase}/xml?invoiceId={invoiceId}", ct);

    /// <summary>A purchase invoice rendered as PDF.</summary>
    public Task<SefResult<byte[]>> GetPurchaseInvoicePdfAsync(long invoiceId, CancellationToken ct = default) =>
        GetBytesAsync($"{Purchase}/pdf?invoiceId={invoiceId}", ct);

    /// <summary>Accepts or rejects a purchase invoice; the comment matters on a rejection.</summary>
    public Task<SefResult<string>> AcceptPurchaseInvoiceAsync(
        long invoiceId, bool accepted, string? comment, CancellationToken ct = default) =>
        PostJsonAsync($"{Purchase}/acceptRejectPurchaseInvoice",
            new { invoiceId, accepted, comment }, ct);

    // ---- Reference data -------------------------------------------------------------------

    /// <summary>SEF's list of accepted units of measure.</summary>
    public Task<SefResult<string>> GetUnitMeasuresAsync(CancellationToken ct = default) =>
        GetAsync($"{Public}/get-unit-measures", ct);

    /// <summary>SEF's list of VAT exemption reasons.</summary>
    public Task<SefResult<string>> GetVatExemptionReasonsAsync(CancellationToken ct = default) =>
        GetAsync($"{Sales}/getValueAddedTaxExemptionReasonList", ct);

    /// <summary>Whether a company is registered on eFaktura, by tax number.</summary>
    public Task<SefResult<string>> CheckCompanyRegisteredAsync(
        string vatNumber, CancellationToken ct = default) =>
        GetAsync($"{Public}/Company/CheckIfCompanyRegisteredOnEfaktura?vatNumber={Uri.EscapeDataString(vatNumber)}", ct);

    /// <summary>The version of the eFaktura system.</summary>
    public Task<SefResult<string>> GetEfakturaVersionAsync(CancellationToken ct = default) =>
        GetAsync($"{Public}/getEfakturaVersion", ct);

    /// <summary>Registers a callback address for SEF's notifications.</summary>
    public Task<SefResult<string>> SubscribeAsync(string callbackUrl, CancellationToken ct = default) =>
        PostJsonAsync($"{Public}/subscribe", new { CallbackUrl = callbackUrl }, ct);

    // ---- VAT recording, V2 ----------------------------------------------------------------

    /// <summary>Creates an individual VAT record (V2).</summary>
    public Task<SefResult<string>> CreateIndividualVatRecordAsync(object payload, CancellationToken ct = default) =>
        PostJsonAsync($"{VatV2}/individual", payload, ct);

    /// <summary>One individual VAT record (V2).</summary>
    public Task<SefResult<string>> GetIndividualVatRecordAsync(long id, CancellationToken ct = default) =>
        GetAsync($"{VatV2}/individual/{id}", ct);

    /// <summary>Cancels an individual VAT record (V2).</summary>
    public Task<SefResult<string>> CancelIndividualVatRecordAsync(long id, CancellationToken ct = default) =>
        PostJsonAsync($"{VatV2}/individual/cancel/{id}", new { }, ct);

    /// <summary>Corrects an individual VAT record (V2).</summary>
    public Task<SefResult<string>> CorrectIndividualVatRecordAsync(
        long id, object payload, CancellationToken ct = default) =>
        PostJsonAsync($"{VatV2}/individual/correction/{id}", payload, ct);

    /// <summary>An individual VAT record as PDF (V2).</summary>
    public Task<SefResult<byte[]>> GetIndividualVatRecordPdfAsync(long id, CancellationToken ct = default) =>
        GetBytesAsync($"{VatV2}/individual/{id}/pdf", ct);

    /// <summary>Creates a group VAT record (V2).</summary>
    public Task<SefResult<string>> CreateGroupVatRecordAsync(object payload, CancellationToken ct = default) =>
        PostJsonAsync($"{VatV2}/group", payload, ct);

    /// <summary>One group VAT record (V2).</summary>
    public Task<SefResult<string>> GetGroupVatRecordAsync(long id, CancellationToken ct = default) =>
        GetAsync($"{VatV2}/group/{id}", ct);

    /// <summary>Cancels a group VAT record (V2).</summary>
    public Task<SefResult<string>> CancelGroupVatRecordAsync(long id, CancellationToken ct = default) =>
        PostJsonAsync($"{VatV2}/group/cancel/{id}", new { }, ct);

    /// <summary>Corrects a group VAT record (V2).</summary>
    public Task<SefResult<string>> CorrectGroupVatRecordAsync(
        long id, object payload, CancellationToken ct = default) =>
        PostJsonAsync($"{VatV2}/group/correction/{id}", payload, ct);

    /// <summary>A group VAT record as PDF (V2).</summary>
    public Task<SefResult<byte[]>> GetGroupVatRecordPdfAsync(long id, CancellationToken ct = default) =>
        GetBytesAsync($"{VatV2}/group/{id}/pdf", ct);

    // ---- Plumbing -------------------------------------------------------------------------

    /// <summary>A read. Nothing it does can leave the tax authority in a state we cannot see.</summary>
    private async Task<SefResult<string>> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        // Awaited rather than returned: `using` disposes the request when this method exits, and a
        // returned task has not sent anything yet — the send would find a disposed request.
        return await ExchangeAsync(request, changesState: false, ct);
    }

    /// <summary>
    /// Every posted payload in this API changes something — accepting a purchase invoice,
    /// cancelling a sale, recording VAT. Declared as such once here rather than at each caller.
    /// </summary>
    private async Task<SefResult<string>> PostJsonAsync(string path, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonBody(payload) };

        // Awaited for the same reason as in GetAsync: the body is disposed with the request.
        return await ExchangeAsync(request, changesState: true, ct);
    }

    private static StringContent JsonBody(object payload)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        return content;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// A document — a PDF, a UBL — rather than a JSON answer.
    ///
    /// A read, always, so a timeout here is an ordinary failure: nothing was changed and asking
    /// again is safe.
    /// </summary>
    private async Task<SefResult<byte[]>> GetBytesAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (!TryPrepare(request, out var problem))
            return SefResult<byte[]>.Fail(SefError.Local(problem, []));

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

            timeout.CancelAfter(Timeout);

            using var response = await httpClient.SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(timeout.Token);

                return SefResult<byte[]>.Fail(SefErrorReader.Read((int)response.StatusCode, error), error);
            }

            return SefResult<byte[]>.Ok(await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        // OperationCanceledException rather than only TaskCanceledException, and only when the
        // caller did not ask for it: a timeout while the body is being read throws the base type,
        // and the caller's own cancellation has to reach them as an exception.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SefResult<byte[]>.Transport($"SEF did not answer within {Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException exception)
        {
            return SefResult<byte[]>.Transport(exception.Message);
        }
    }

    /// <summary>
    /// The one place a request is actually made.
    /// </summary>
    /// <param name="changesState">
    /// Whether the request, if it arrived, would have altered something at the tax authority —
    /// filing an invoice, cancelling one, a storno.
    ///
    /// This is the whole reason the parameter exists. On a read, a timeout means nothing happened
    /// and asking again is free. On a send, it means the invoice may be filed, and sending it
    /// again would put a second one under the same number at the state. Same exception, opposite
    /// correct action — so the caller declares which kind of request it is rather than the
    /// exception handler guessing.
    /// </param>
    /// <param name="request">The request to send; authentication headers are added here.</param>
    /// <param name="ct">The caller's token, kept apart from the timeout so the two can be told apart.</param>
    private async Task<SefResult<string>> ExchangeAsync(
        HttpRequestMessage request, bool changesState, CancellationToken ct)
    {
        if (!TryPrepare(request, out var problem))
            return SefResult<string>.Fail(SefError.Local(problem, []));

        try
        {
            // The configured timeout, applied per call: HttpClient.Timeout is fixed when the
            // client is built, and this one comes from settings a person can change.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

            timeout.CancelAfter(Timeout);

            using var response = await httpClient.SendAsync(request, timeout.Token);

            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (response.IsSuccessStatusCode) return SefResult<string>.Ok(body, body, (int)response.StatusCode);

            // A 5xx on a send is not a refusal: SEF was reached and fell over somewhere inside, so
            // a storno applied before it failed is indistinguishable from one that never ran. On a
            // read the same status is simply a failure worth retrying.
            if (changesState && (int)response.StatusCode >= 500)
            {
                return SefResult<string>.Unknown(
                    $"SEF returned {(int)response.StatusCode}.", (int)response.StatusCode, body);
            }

            return SefResult<string>.Fail(
                SefErrorReader.Read((int)response.StatusCode, body), body);
        }
        // OperationCanceledException rather than only TaskCanceledException: SendAsync throws the
        // latter on a timeout, but a timeout while the body is still being read throws the base
        // type, and a submission whose reply was cut off is exactly the case that must come back
        // as Unknown rather than as an exception.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var message = $"SEF did not answer within {Timeout.TotalSeconds:0} s.";

            return changesState
                ? SefResult<string>.Unknown(message)
                : SefResult<string>.Transport(message);
        }
        catch (HttpRequestException exception)
        {
            // A connection that never opened changed nothing, whatever the request was for: the
            // name did not resolve, the port refused, the TLS handshake failed. Nothing was sent,
            // so nothing can be filed, and the right answer is a retryable failure — an Unknown
            // here would forbid the resend and leave the host reconciling an invoice SEF never
            // received. A connection cut after it opened is the other case: SEF may have read the
            // request, and on a send that is Unknown, the same question as a timeout.
            return changesState && !NeverConnected(exception)
                ? SefResult<string>.Unknown(exception.Message)
                : SefResult<string>.Transport(exception.Message);
        }
    }

    private static bool NeverConnected(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError;

    private TimeSpan Timeout =>
        TimeSpan.FromSeconds(Math.Clamp(credentials.TimeoutSeconds, 1, 600));

    /// <summary>
    /// Builds the address and sets the headers. A base URL that is not a URL, or a key that is not
    /// a legal header value, is a settings mistake: it comes back as a failure the host can show,
    /// not as an exception out of a client whose other failures are all results.
    /// </summary>
    private bool TryPrepare(HttpRequestMessage request, out string problem)
    {
        try
        {
            request.RequestUri = credentials.Endpoint(request.RequestUri!.ToString());

            request.Headers.Add("ApiKey", credentials.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            problem = "";

            return true;
        }
        catch (Exception exception) when (exception is UriFormatException or FormatException)
        {
            problem = $"SEF address or key is not usable: {exception.Message}";

            return false;
        }
    }

    private async Task<SefResult<IReadOnlyList<SefStatusChange>>> ChangesAsync(
        string path, string idField, CancellationToken ct)
    {
        var body = await GetChangesBodyAsync(path, ct);

        if (!body.IsSuccess) return Carry<string, IReadOnlyList<SefStatusChange>>(body);

        return SefResult<IReadOnlyList<SefStatusChange>>.Ok(
            SefResponseReader.ReadChanges(body.Value!, idField), body.ResponseBody, body.HttpStatusCode);
    }

    /// <summary>Changes is a POST despite reading data — that is how the API defines it.</summary>
    private async Task<SefResult<string>> GetChangesBodyAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Empty() };

        // A POST that reads: the method is the API's choice, and a timeout here changes nothing.
        return await ExchangeAsync(request, changesState: false, ct);
    }

    /// <summary>
    /// An explicitly empty body for the two POSTs that carry no payload, because their parameters
    /// are in the query string.
    ///
    /// Without it `HttpClient` sends no `Content-Length` at all, and the server in front of SEF
    /// answers **411 Length Required** before the request reaches anything that would look at the
    /// API key. Both of these calls were therefore incapable of succeeding against the real
    /// service, in a way no unit test could show: a test double is handed the request object and
    /// never sees the header that was not written.
    ///
    /// Found by calling the sandbox. 411 with a valid key and 411 with a deliberately invalid one
    /// — identical answers, which is what said the failure was below authentication rather than
    /// about it.
    /// </summary>
    private static ByteArrayContent Empty() => new([]);

    private async Task<SefResult<IReadOnlyList<long>>> IdsAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Empty() };
        var body = await ExchangeAsync(request, changesState: false, ct);

        if (!body.IsSuccess) return Carry<string, IReadOnlyList<long>>(body);

        return SefResult<IReadOnlyList<long>>.Ok(
            SefResponseReader.ReadIds(body.Value!), body.ResponseBody, body.HttpStatusCode);
    }

    private static string Iso(DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
