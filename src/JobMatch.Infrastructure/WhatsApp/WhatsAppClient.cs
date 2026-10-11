using System.Net.Http.Json;
using System.Text.Json;
using JobMatch.Domain.Abstractions;
using JobMatch.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobMatch.Infrastructure.WhatsApp;

/// <summary>
/// Typed WhatsApp Cloud API client. Sends text messages and marks inbound messages read
/// via the Meta Graph API. The <see cref="HttpClient"/> base address + auth header are
/// configured at registration time.
/// </summary>
public class WhatsAppClient : IWhatsAppClient
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppClient> _logger;

    public WhatsAppClient(HttpClient http, IOptions<WhatsAppOptions> options, ILogger<WhatsAppClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    private string MessagesPath => $"/{_options.GraphApiVersion}/{_options.PhoneNumberId}/messages";

    public async Task<string?> SendTextAsync(string waId, string text, CancellationToken ct)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = waId,
            type = "text",
            text = new { preview_url = false, body = text }
        };

        using var resp = await _http.PostAsJsonAsync(MessagesPath, payload, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("WhatsApp send failed ({Status}): {Body}", (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0)
            return msgs[0].GetProperty("id").GetString();
        return null;
    }

    public async Task MarkReadAsync(string waMessageId, CancellationToken ct)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            status = "read",
            message_id = waMessageId
        };
        using var resp = await _http.PostAsJsonAsync(MessagesPath, payload, ct);
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("Mark-as-read failed for {MessageId} ({Status})", waMessageId, (int)resp.StatusCode);
    }
}

/// <summary>
/// No-network stand-in used when WhatsApp credentials are not configured (local dev /
/// Slice 1). Logs outbound sends and returns a synthetic message id.
/// </summary>
public class LoggingWhatsAppClient : IWhatsAppClient
{
    private readonly ILogger<LoggingWhatsAppClient> _logger;
    private int _counter;

    public LoggingWhatsAppClient(ILogger<LoggingWhatsAppClient> logger) => _logger = logger;

    public Task<string?> SendTextAsync(string waId, string text, CancellationToken ct)
    {
        var id = $"local-out-{Interlocked.Increment(ref _counter)}";
        _logger.LogInformation("[WhatsApp:DRYRUN] -> {WaId}: {Text}", waId, text);
        return Task.FromResult<string?>(id);
    }

    public Task MarkReadAsync(string waMessageId, CancellationToken ct)
    {
        _logger.LogInformation("[WhatsApp:DRYRUN] mark-read {MessageId}", waMessageId);
        return Task.CompletedTask;
    }
}
