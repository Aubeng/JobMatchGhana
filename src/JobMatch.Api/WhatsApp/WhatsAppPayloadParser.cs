using System.Text.Json;
using JobMatch.Domain.Conversation;

namespace JobMatch.Api.WhatsApp;

/// <summary>
/// Parses a Meta webhook payload into normalised <see cref="InboundMessage"/> values.
/// Only user-initiated <c>text</c> messages are handled in the pilot; other message
/// types are surfaced with their type so the FSM can issue a repair prompt.
/// Status callbacks (delivered/read) carry no <c>messages</c> array and yield nothing.
/// </summary>
public static class WhatsAppPayloadParser
{
    public static IReadOnlyList<InboundMessage> Parse(ReadOnlySpan<byte> rawBody)
    {
        var results = new List<InboundMessage>();
        using var doc = JsonDocument.Parse(rawBody.ToArray());

        if (!doc.RootElement.TryGetProperty("entry", out var entries)) return results;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value)) continue;
                if (!value.TryGetProperty("messages", out var messages)) continue; // e.g. status callbacks

                // Contact display name (best effort) for profile prefill.
                string? contactName = null;
                if (value.TryGetProperty("contacts", out var contacts) && contacts.GetArrayLength() > 0
                    && contacts[0].TryGetProperty("profile", out var profile)
                    && profile.TryGetProperty("name", out var nameEl))
                    contactName = nameEl.GetString();

                foreach (var msg in messages.EnumerateArray())
                {
                    var waId = msg.TryGetProperty("from", out var from) ? from.GetString() : null;
                    var id = msg.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var type = msg.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "unknown";
                    if (string.IsNullOrEmpty(waId) || string.IsNullOrEmpty(id)) continue;

                    var text = ExtractText(msg, type);
                    var timestamp = ExtractTimestamp(msg);

                    results.Add(new InboundMessage(waId!, id!, text, type ?? "unknown", contactName, timestamp));
                }
            }
        }

        return results;
    }

    private static string ExtractText(JsonElement msg, string? type) => type switch
    {
        "text" when msg.TryGetProperty("text", out var t) && t.TryGetProperty("body", out var body)
            => body.GetString() ?? string.Empty,
        // Interactive button/list replies carry the chosen id/title.
        "interactive" when msg.TryGetProperty("interactive", out var i)
            => ExtractInteractive(i),
        "button" when msg.TryGetProperty("button", out var b) && b.TryGetProperty("text", out var bt)
            => bt.GetString() ?? string.Empty,
        _ => string.Empty
    };

    /// <summary>
    /// A tapped button or list row yields its <c>id</c>: the same value a worker could type
    /// ("1", "done"), so the FSM handles taps and typing identically. Title only as a fallback.
    /// </summary>
    private static string ExtractInteractive(JsonElement interactive)
    {
        foreach (var kind in new[] { "button_reply", "list_reply" })
        {
            if (!interactive.TryGetProperty(kind, out var reply)) continue;
            if (reply.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                return value;
            return reply.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        }
        return string.Empty;
    }

    private static DateTimeOffset ExtractTimestamp(JsonElement msg)
    {
        if (msg.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
            && long.TryParse(ts.GetString(), out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        return DateTimeOffset.UtcNow;
    }
}
