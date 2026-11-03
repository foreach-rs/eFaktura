namespace ForEach.Infrastructure.External.eFaktura;

/// <summary>
/// How a call to SEF ended, in the three states a caller must tell apart.
/// </summary>
public enum SefOutcome
{
    /// <summary>SEF accepted the call.</summary>
    Succeeded,

    /// <summary>It did not happen. Safe to try again once whatever caused it is dealt with.</summary>
    Failed,

    /// <summary>
    /// Nobody knows. A state-changing request timed out or the connection was cut after it was
    /// sent — the invoice may be filed on SEF and may not, and the one thing a caller must not do
    /// is send it again. This state exists so that decision is never taken by accident.
    /// </summary>
    Unknown
}

/// <summary>What a caller should DO about a failure.</summary>
public enum SefFailureKind
{
    /// <summary>The API key, or the wrong environment. It repeats until somebody changes it.</summary>
    Configuration,

    /// <summary>The document or the request was wrong — validation, or an action the status forbids.</summary>
    CallerMistake,

    /// <summary>SEF-side or a rate limit. The next attempt will probably work.</summary>
    Transient
}

/// <summary>
/// What SEF says when it refuses.
/// </summary>
/// <param name="Details">
/// Their validation messages, verbatim and in order. On a rejected invoice this is the whole of
/// the useful content — "polje BT-31 nije popunjeno" tells an accountant what to fix, and a
/// summary of it tells them nothing.
/// </param>
/// <param name="HttpStatusCode">The status SEF answered with, or zero for a refusal decided locally.</param>
/// <param name="Code">SEF's own error code, or null when it sent none.</param>
/// <param name="Message">One sentence describing the refusal.</param>
public sealed record SefError(
    int HttpStatusCode, string? Code, string Message, IReadOnlyList<string> Details)
{
    /// <summary>
    /// A refusal decided here rather than by SEF — a document that never left this machine.
    ///
    /// Status zero because there was no request: inventing a 400 would put a status code in the
    /// stored response body that SEF never sent, and six months later the question "what did SEF
    /// say" has to have one honest answer.
    /// </summary>
    public static SefError Local(string message, IReadOnlyList<string> details) =>
        new(0, "InvalidDocument", message, details);

    /// <summary>What a caller should do about this refusal, derived from the status.</summary>
    public SefFailureKind Kind => HttpStatusCode switch
    {
        // Never sent, and wrong here. Retrying would present the same document again.
        0 => SefFailureKind.CallerMistake,

        // The key is wrong, expired, or belongs to the other environment — a demo key against
        // production answers exactly like a revoked one, and both need a person.
        401 or 403 => SefFailureKind.Configuration,

        // UBL that does not validate, or an action the invoice's status does not allow: cancelling
        // one already cancelled, storno on a draft. Retrying sends the same thing again.
        // 404 is a wrong id: the invoice SEF was asked about does not exist, and asking again
        // for the same one is asking the same question.
        400 or 404 or 409 or 422 => SefFailureKind.CallerMistake,

        408 or 429 or >= 500 => SefFailureKind.Transient,

        // Same reasoning as the other two libraries: an unrecognised failure is retried, because a
        // wasted attempt costs one scheduled run and giving up costs an invoice nobody filed.
        _ => SefFailureKind.Transient
    };

    /// <summary>The refusal as one line: status, code, message, then the details.</summary>
    public override string ToString()
    {
        if (HttpStatusCode == 0)
            return Details.Count == 0 ? Message : $"{Message} — {string.Join("; ", Details)}";

        var head = Code is { Length: > 0 } code
            ? $"SEF {HttpStatusCode} ({code}): {Message}"
            : $"SEF {HttpStatusCode}: {Message}";

        return Details.Count == 0 ? head : $"{head} — {string.Join("; ", Details)}";
    }
}

/// <summary>
/// The outcome of one SEF call.
///
/// Its own type rather than FluentResults, like the other two ForEach libraries: no third-party
/// dependency, so a host on a different result type adapts it in a dozen lines.
///
/// What is unusual here is <see cref="SefOutcome.Unknown"/>, and it is unusual because filing an
/// invoice is not idempotent by nature. Everything else in this class exists to make sure a
/// caller can never confuse "it failed" with "we do not know".
/// </summary>
/// <remarks>
/// <see cref="ResponseBody"/> is kept on every outcome, success included, because the host stores
/// it: when an accountant asks in six months why an invoice was rejected, the answer has to be
/// what SEF actually said rather than what was made of it.
/// </remarks>
public sealed class SefResult<T>
{
    private SefResult(
        SefOutcome outcome, T? value, SefError? error, string? transportFailure,
        int? httpStatusCode, string? responseBody)
    {
        Outcome = outcome;
        Value = value;
        Error = error;
        TransportFailure = transportFailure;
        HttpStatusCode = httpStatusCode;
        ResponseBody = responseBody;
    }

    /// <summary>How the call ended.</summary>
    public SefOutcome Outcome { get; }

    /// <summary>What the call returned. Set only on success.</summary>
    public T? Value { get; }

    /// <summary>SEF's own refusal. Null on success, and null when nothing answered at all.</summary>
    public SefError? Error { get; }

    /// <summary>Set when the call did not get an answer — timeout, DNS, TLS, a cut connection.</summary>
    public string? TransportFailure { get; }

    /// <summary>The status SEF answered with. Null when nothing answered.</summary>
    public int? HttpStatusCode { get; }

    /// <summary>Whatever came back, unparsed. Kept on every outcome; see the remarks above.</summary>
    public string? ResponseBody { get; }

    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess => Outcome == SefOutcome.Succeeded;

    /// <summary>
    /// One sentence, in SEF's words where there are any.
    ///
    /// The host writes this onto the submission row and shows it to whoever pressed send, so it is
    /// worth being the server's message: "faktura sa istim brojem već postoji" tells somebody what
    /// to do, and "slanje nije uspelo" does not.
    /// </summary>
    public string FailureMessage => Error?.ToString() ?? TransportFailure ?? "";

    /// <summary>
    /// Whether trying again is safe and worth it.
    ///
    /// False for Unknown, always, and that is the point of the property: the request may have been
    /// filed, so a retry would be a second invoice with the same number at the tax authority.
    /// </summary>
    public bool IsWorthRetrying =>
        Outcome == SefOutcome.Failed
        && (TransportFailure is not null || Error?.Kind == SefFailureKind.Transient);

    /// <summary>A call that succeeded.</summary>
    public static SefResult<T> Ok(T value, string? responseBody = null, int? httpStatusCode = null) =>
        new(SefOutcome.Succeeded, value, null, null, httpStatusCode, responseBody);

    /// <summary>A call SEF answered with a refusal.</summary>
    public static SefResult<T> Fail(SefError error, string? responseBody = null) =>
        new(SefOutcome.Failed, default, error, null, error.HttpStatusCode, responseBody);

    /// <summary>A call that never got an answer, on a request that changed nothing.</summary>
    public static SefResult<T> Transport(string message) =>
        new(SefOutcome.Failed, default, null, message, null, null);

    /// <summary>
    /// A call that never got an answer, on a request that may have changed something.
    ///
    /// Reserved for state-changing sends. Everything a host does about it — reconciling by
    /// requestId, refusing to resend — hangs off this one distinction.
    /// </summary>
    public static SefResult<T> Unknown(
        string message, int? httpStatusCode = null, string? responseBody = null) =>
        new(SefOutcome.Unknown, default, null, message, httpStatusCode, responseBody);
}
