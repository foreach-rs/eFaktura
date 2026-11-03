namespace ForEach.Infrastructure.External.eFaktura;

/// <summary>
/// What SEF needs on every call: an address and a key.
///
/// There is no handshake and no token exchange — the key goes in an `ApiKey` header on every
/// request, and HTTPS is the whole of the protection. That is their design rather than a
/// simplification here.
///
/// The environment is part of the credentials rather than a flag beside them, because the key and
/// the address belong together: a demo key against production is refused with the same 401 as a
/// revoked one, and the pair is what makes that impossible to get half right.
/// </summary>
public sealed class SefCredentials
{
    /// <summary>The live system. Invoices filed here are filed with the tax authority.</summary>
    public const string ProductionBaseUrl = "https://efaktura.mfin.gov.rs";

    /// <summary>
    /// The demo system. A separate account, a separate key, and nothing filed on it is real —
    /// which is what makes it the only place to try a document shape for the first time.
    /// </summary>
    public const string DemoBaseUrl = "https://demoefaktura.mfin.gov.rs";

    /// <summary>SEF's address — <see cref="ProductionBaseUrl"/> or <see cref="DemoBaseUrl"/>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Sent as the `ApiKey` header. A secret, and treated as one by whoever binds it.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Sixty seconds by default, and generous on purpose: SEF validates a document while the
    /// request is open, and a timeout on a send is the one failure with no safe recovery.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Both present. Nothing here checks whether the key is CORRECT — only SEF can say that, and
    /// it says it as a 401. What this answers is whether a call is worth making at all, so a host
    /// can run dark until somebody has been issued a key.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Whether this is the live system, by address.
    ///
    /// Worth asking before a destructive test: a host that offers "send a sample invoice" wants to
    /// know it is not about to file one with the state.
    /// </summary>
    public bool IsProduction =>
        BaseUrl.TrimEnd('/').Equals(ProductionBaseUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>One endpoint's address, with the slashes sorted out once.</summary>
    public Uri Endpoint(string path) => new($"{BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
}
