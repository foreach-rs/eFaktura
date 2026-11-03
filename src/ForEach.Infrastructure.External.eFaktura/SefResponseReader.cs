using System.Globalization;
using System.Text.Json;

using ForEach.Infrastructure.External.eFaktura.Models;

namespace ForEach.Infrastructure.External.eFaktura;

/// <summary>
/// Reading SEF's replies.
///
/// Lifted out of <c>SefClient</c> for the reason <c>JobSchedule</c> and
/// <c>EOtpremnicaResponseReader</c> were: it is a rule, and the class holding it needs an
/// <c>HttpClient</c>. Every SEF test in the project runs against a fake client that supplies the
/// changes directly, so this was the one part of the status path nothing exercised — and it is the
/// part that decides whether an invoice reads as Approved or Rejected by the tax authority.
/// </summary>
public static class SefResponseReader
{
    /// <summary>
    /// The status changes in a changes-page body, or an empty list when the body is not an array.
    ///
    /// An empty list rather than a failure, deliberately: "nothing has changed" is the ordinary
    /// answer to this call, and a poll that treated it as an error would log a failure every few
    /// minutes on a healthy system until somebody stopped reading the log.
    /// </summary>
    public static IReadOnlyList<SefStatusChange> ReadChanges(string? body, string idField)
    {
        var changes = new List<SefStatusChange>();

        if (string.IsNullOrWhiteSpace(body)) return changes;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Array) return changes;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                changes.Add(new SefStatusChange(
                    Long(entry, "EventId") ?? 0,
                    Date(entry, "Date") ?? DateTime.MinValue,
                    Text(entry, "NewInvoiceStatus"),
                    Long(entry, idField) ?? 0,
                    Text(entry, "Comment"),
                    Long(entry, "CirInvoiceId")));
            }
        }
        catch (JsonException)
        {
            // Not JSON. The caller keeps whatever it already knew.
        }

        return changes;
    }

    /// <summary>The numbers in a bare id-list body, ignoring anything in it that is not one.</summary>
    public static IReadOnlyList<long> ReadIds(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            return
            [
                .. document.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Number)
                    .Select(e => e.GetInt64())
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The first of <paramref name="names"/> present as a number on the root object, or null.
    ///
    /// This is how an invoice learns the id SEF filed it under, so null is not a detail: the
    /// caller keeps the raw body and the invoice has no SEF id, which is what a later status poll
    /// matches on. Root only — a number of the same name nested somewhere else is a different
    /// thing, and taking it would file the invoice under an id the register never issued.
    /// </summary>
    public static long? ReadId(string? body, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var name in names)
            {
                if (document.RootElement.TryGetProperty(name, out var property)
                    && property.ValueKind == JsonValueKind.Number)
                {
                    return property.GetInt64();
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON — the caller keeps the raw body.
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    /// <summary>
    /// A timestamp from SEF, read the way <c>Iso</c> writes them: invariant, and pinned to UTC.
    ///
    /// The culture is not a formality here. `TryParse` without one uses the server's, and
    /// `CultureIndependenceTests` already records what that costs on the eOtpremnica side — the
    /// separators in an ISO string are literal, so those were never the risk; the *calendar* was.
    /// Under a culture whose default calendar is not Gregorian the same text becomes a date
    /// centuries away, on a value that decides which invoice event is newer.
    ///
    /// `AdjustToUniversal | AssumeUniversal` for the second half: SEF timestamps are UTC, and
    /// without this a string carrying an offset comes back <c>Local</c>. Npgsql refuses a
    /// Local-kind value for a <c>timestamptz</c> column, so it is the difference between a stored
    /// event and a throw.
    /// </summary>
    private static DateTime? Date(JsonElement element, string name) =>
        DateTime.TryParse(
            Text(element, name),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
}
