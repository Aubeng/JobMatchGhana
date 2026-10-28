using System.Net.Http.Json;
using System.Text.Json;
using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
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

    public async Task<string?> SendAsync(string waId, OutboundMessage message, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(MessagesPath, BuildPayload(waId, message), ct);
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

    public async Task<string?> SendTemplateAsync(string waId, string templateName, string languageCode,
        IReadOnlyList<string> bodyParameters, string? quickReplyPayload, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(MessagesPath,
            BuildTemplatePayload(waId, templateName, languageCode, bodyParameters, quickReplyPayload), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("WhatsApp template {Template} failed ({Status}): {Body}", templateName, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0
            ? msgs[0].GetProperty("id").GetString() : null;
    }

    /// <summary>Graph API template payload: body parameters plus an optional quick-reply payload.</summary>
    public static object BuildTemplatePayload(string waId, string templateName, string languageCode,
        IReadOnlyList<string> bodyParameters, string? quickReplyPayload)
    {
        var components = new List<object>
        {
            new { type = "body", parameters = bodyParameters.Select(p => new { type = "text", text = p }) }
        };
        if (quickReplyPayload is not null)
            components.Add(new
            {
                type = "button", sub_type = "quick_reply", index = "0",
                parameters = new[] { new { type = "payload", payload = quickReplyPayload } }
            });
        return new
        {
            messaging_product = "whatsapp",
            to = waId,
            type = "template",
            template = new { name = templateName, language = new { code = languageCode }, components }
        };
    }

    /// <summary>
    /// Graph API payload: text, interactive "button" (max 3 replies, titles &lt;= 20 chars) or
    /// interactive "list" (max 10 rows, titles &lt;= 24, descriptions &lt;= 72). Over-long titles
    /// are trimmed here so a wording change can never make Meta reject a message.
    /// </summary>
    public static object BuildPayload(string waId, OutboundMessage message)
    {
        object content;
        string type;
        if (message.Buttons is { Count: > 0 } buttons)
        {
            type = "interactive";
            content = new
            {
                type = "button",
                body = new { text = message.Text },
                action = new
                {
                    buttons = buttons.Take(3).Select(b => new { type = "reply", reply = new { id = b.Id, title = Cut(b.Title, 20) } })
                }
            };
        }
        else if (message.List is { Count: > 0 } rows)
        {
            type = "interactive";
            content = new
            {
                type = "list",
                body = new { text = message.Text },
                action = new
                {
                    button = Cut(message.ListButton ?? "Choose", 20),
                    sections = new[]
                    {
                        new
                        {
                            title = "Options",
                            rows = rows.Take(10).Select(r => r.Description is null
                                ? (object)new { id = r.Id, title = Cut(r.Title, 24) }
                                : new { id = r.Id, title = Cut(r.Title, 24), description = Cut(r.Description, 72) })
                        }
                    }
                }
            };
        }
        else
        {
            type = "text";
            content = new { preview_url = false, body = message.Text };
        }

        var payload = new Dictionary<string, object>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = waId,
            ["type"] = type,
            [type] = content
        };
        return payload;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public async Task<string?> SendDocumentAsync(string waId, byte[] content, string fileName, string mimeType,
        string? caption, CancellationToken ct)
    {
        // 1) Upload the file to WhatsApp's media store (kept by Meta for 30 days).
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("whatsapp"), "messaging_product");
        form.Add(new StringContent(mimeType), "type");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        form.Add(file, "file", fileName);
        using var upload = await _http.PostAsync($"/{_options.GraphApiVersion}/{_options.PhoneNumberId}/media", form, ct);
        var uploaded = await upload.Content.ReadAsStringAsync(ct);
        if (!upload.IsSuccessStatusCode)
        {
            _logger.LogError("WhatsApp media upload failed ({Status}): {Body}", (int)upload.StatusCode, uploaded);
            upload.EnsureSuccessStatusCode();
        }
        string? mediaId;
        using (var up = JsonDocument.Parse(uploaded))
            mediaId = up.RootElement.GetProperty("id").GetString();

        // 2) Send it as a document message.
        using var resp = await _http.PostAsJsonAsync(MessagesPath, BuildDocumentPayload(waId, mediaId!, fileName, caption), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("WhatsApp document send failed ({Status}): {Body}", (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0
            ? msgs[0].GetProperty("id").GetString() : null;
    }

    public static object BuildDocumentPayload(string waId, string mediaId, string fileName, string? caption) => new
    {
        messaging_product = "whatsapp",
        recipient_type = "individual",
        to = waId,
        type = "document",
        document = caption is null
            ? (object)new { id = mediaId, filename = fileName }
            : new { id = mediaId, filename = fileName, caption }
    };

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

    public Task<string?> SendAsync(string waId, OutboundMessage message, CancellationToken ct)
    {
        var id = $"local-out-{Interlocked.Increment(ref _counter)}";
        _logger.LogInformation("[WhatsApp:DRYRUN] -> {WaId}: {Text}", waId, MessageRendering.ForLog(message));
        return Task.FromResult<string?>(id);
    }

    public Task<string?> SendTemplateAsync(string waId, string templateName, string languageCode,
        IReadOnlyList<string> bodyParameters, string? quickReplyPayload, CancellationToken ct)
    {
        var id = $"local-out-{Interlocked.Increment(ref _counter)}";
        _logger.LogInformation("[WhatsApp:DRYRUN] -> {WaId}: template {Template}({Params})", waId, templateName,
            string.Join(", ", bodyParameters));
        return Task.FromResult<string?>(id);
    }

    public Task MarkReadAsync(string waMessageId, CancellationToken ct)
    {
        _logger.LogInformation("[WhatsApp:DRYRUN] mark-read {MessageId}", waMessageId);
        return Task.CompletedTask;
    }

    public Task<string?> SendDocumentAsync(string waId, byte[] content, string fileName, string mimeType, string? caption,
        CancellationToken ct)
    {
        var id = $"local-out-{Interlocked.Increment(ref _counter)}";
        _logger.LogInformation("[WhatsApp:DRYRUN] -> {WaId}: document {FileName} ({Bytes} bytes)", waId, fileName, content.Length);
        return Task.FromResult<string?>(id);
    }
}
