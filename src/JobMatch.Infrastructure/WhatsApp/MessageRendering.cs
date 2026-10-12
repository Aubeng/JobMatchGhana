using JobMatch.Domain.Conversation;

namespace JobMatch.Infrastructure.WhatsApp;

/// <summary>Helpers for logging and splitting outbound messages.</summary>
public static class MessageRendering
{
    /// <summary>
    /// The message as one string for MessageLog / dry-run logs, with its options appended so
    /// the research log shows exactly what the worker could tap.
    /// </summary>
    public static string ForLog(OutboundMessage message)
    {
        var options = message.Buttons ?? message.List;
        return options is { Count: > 0 }
            ? $"{message.Text}\n[{(message.Buttons is not null ? "buttons" : "list")}: {string.Join(" | ", options.Select(o => $"{o.Id}={o.Title}"))}]"
            : message.Text;
    }

    /// <summary>
    /// NFR-05: no outbound message over <see cref="OutboundMessage.MaxLength"/> characters. A
    /// longer one is split at paragraph (then line, then word) boundaries; buttons or list stay
    /// on the last part, after the text they refer to.
    /// </summary>
    public static IReadOnlyList<OutboundMessage> Split(OutboundMessage message, int max = OutboundMessage.MaxLength)
    {
        if (message.Text.Length <= max) return new[] { message };
        var parts = new List<string>();
        var rest = message.Text;
        while (rest.Length > max)
        {
            var cut = BestCut(rest, max);
            parts.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart('\n', ' ');
        }
        parts.Add(rest);
        return parts.Select((p, i) => i == parts.Count - 1 ? message with { Text = p } : new OutboundMessage(p)).ToList();
    }

    private static int BestCut(string text, int max)
    {
        foreach (var sep in new[] { "\n\n", "\n", " " })
        {
            var i = text.LastIndexOf(sep, max, StringComparison.Ordinal);
            if (i > max / 3) return i;
        }
        return max;
    }
}
