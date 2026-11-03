namespace ForEach.Infrastructure.External.eFaktura.Models;

/// <summary>
/// Whether the invoice is also filed in the Central Invoice Register.
///
/// Only meaningful for public-sector buyers, and omitted rather than sent as "No" when it does not
/// apply: a wrong explicit value is worse than its absence, because it is a statement about who
/// the buyer is.
/// </summary>
public enum SefCirMode
{
    /// <summary>Leave the parameter out. The default, and right for any buyer outside the public sector.</summary>
    NotSpecified,
    /// <summary>Also file the invoice in the Central Invoice Register.</summary>
    Yes,
    /// <summary>State explicitly that it is not to be filed there.</summary>
    No
}

/// <summary>
/// What SEF returns when it accepts a document.
/// </summary>
/// <param name="ResponseBody">
/// The whole answer, unparsed. Carried on the record as well as on the result because a host
/// stores it against the submission — six months later, "what exactly did SEF say when this was
/// filed" is a question with one correct answer.
/// </param>
/// <param name="InvoiceId">SEF's id for the document, the one every later call asks about.</param>
/// <param name="SalesInvoiceId">The id on the sales side, when SEF returns one.</param>
/// <param name="PurchaseInvoiceId">The id on the purchase side, when SEF returns one.</param>
public sealed record SefInvoiceIds(
    long InvoiceId, long? SalesInvoiceId, long? PurchaseInvoiceId, string ResponseBody);

/// <summary>
/// One status change, as the changes endpoint reports it.
/// </summary>
/// <param name="NewInvoiceStatus">
/// SEF's own word for it, unmapped. A host translates it into whatever its own column holds — and
/// keeping the raw string here is what lets it recognise a status this library has never heard of
/// rather than turning it into a guess.
/// </param>
/// <param name="Comment">
/// Why, when a buyer rejects an invoice. The one field on this record an accountant actually
/// reads, and the reason a rejection is a telephone call rather than a mystery.
/// </param>
/// <param name="EventId">The change's own id, increasing; a host can resume from the last one it saw.</param>
/// <param name="Date">When the change happened.</param>
/// <param name="InvoiceId">The invoice the change belongs to.</param>
/// <param name="CirInvoiceId">The invoice's id in the Central Invoice Register, when it was filed there.</param>
public sealed record SefStatusChange(
    long EventId,
    DateTime Date,
    string? NewInvoiceStatus,
    long InvoiceId,
    string? Comment,
    long? CirInvoiceId);
