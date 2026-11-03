using ForEach.Infrastructure.External.eFaktura.Models;

namespace ForEach.Infrastructure.External.eFaktura;

/// <summary>
/// The words SEF returns, as statuses.
///
/// One vocabulary in one place, because SEF names the same state more than one way — "seen" and
/// "delivered", "cancelled" and "canceled", "approved" and "accepted" — and a consumer writing its
/// own switch gets four of the eight right and never finds out about the rest.
///
/// Null for a word this map has never seen, and that is the whole of the library's opinion: what
/// to do about an unrecognised status is a decision with consequences a host owns. Reading it as
/// "still sending" is right when a document has just been submitted and catastrophic when it is
/// being refreshed — the second erases a buyer's acceptance — and only the caller knows which of
/// those it is doing.
/// </summary>
public static class SefStatusMap
{
    /// <summary>The status for SEF's word, or null for one this map does not know.</summary>
    public static SefInvoiceStatus? Known(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : Map(raw);

    private static SefInvoiceStatus? Map(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "draft" or "new" => SefInvoiceStatus.Draft,
        "sending" or "sent" => SefInvoiceStatus.Sent,
        "seen" or "delivered" => SefInvoiceStatus.Delivered,
        "approved" or "accepted" => SefInvoiceStatus.Approved,
        "rejected" => SefInvoiceStatus.Rejected,
        "cancelled" or "canceled" or "deleted" => SefInvoiceStatus.Cancelled,
        "storno" => SefInvoiceStatus.Storno,
        "mistake" or "error" or "sendingerror" => SefInvoiceStatus.Failed,
        _ => null
    };
}
