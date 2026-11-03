using System.Text.Json;

namespace ForEach.Infrastructure.External.eFaktura;

/// <summary>
/// SEF's refusal, out of whichever shape it arrived in.
///
/// Three shapes are in use across the API — a bare `Message`, an `Errors` array, and a
/// `ValidationErrors` array — and which one comes back depends on the endpoint rather than on the
/// kind of problem. A reader that knew one of them would silently produce "SEF returned 400" for
/// the other two, which is the message that tells an accountant nothing.
/// </summary>
internal static class SefErrorReader
{
    public static SefError Read(int statusCode, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new SefError(statusCode, null, $"SEF returned {statusCode}.", []);

        try
        {
            using var document = JsonDocument.Parse(body);

            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return new SefError(statusCode, null, Clip(body), []);

            var details = new List<string>();

            Collect(root, "Errors", details);
            Collect(root, "errors", details);
            Collect(root, "ValidationErrors", details);
            Collect(root, "validationErrors", details);

            var message = Text(root, "Message")
                          ?? Text(root, "message")
                          ?? Text(root, "title")
                          ?? (details.Count > 0 ? details[0] : $"SEF returned {statusCode}.");

            return new SefError(statusCode, Text(root, "Code") ?? Text(root, "code"), message, details);
        }
        catch (JsonException)
        {
            // HTML from a gateway in front of SEF, most often. Kept as the message rather than
            // discarded: it is the only evidence of where the request actually stopped.
            return new SefError(statusCode, null, Clip(body), []);
        }
    }

    private static void Collect(JsonElement root, string name, List<string> into)
    {
        if (!root.TryGetProperty(name, out var value)) return;

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    var text = item.ValueKind == JsonValueKind.String
                        ? item.GetString()

                        // An object per error, which some endpoints use: the message field inside
                        // it is the sentence somebody has to read.
                        : Text(item, "Message") ?? Text(item, "message") ?? item.ToString();

                    if (!string.IsNullOrWhiteSpace(text)) into.Add(text);
                }

                break;

            case JsonValueKind.String when value.GetString() is { Length: > 0 } single:
                into.Add(single);
                break;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string Clip(string body) => body.Length <= 300 ? body : body[..300] + "…";
}
