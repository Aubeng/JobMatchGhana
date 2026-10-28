using JobMatch.Api.Messaging;
using JobMatch.Infrastructure.Configuration;
using JobMatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JobMatch.Api.WhatsApp;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWhatsAppWebhook(this IEndpointRouteBuilder app)
    {
        // GET: Meta verification handshake. Echo hub.challenge when the verify token matches.
        app.MapGet("/webhook", (HttpContext ctx, IOptions<WhatsAppOptions> options) =>
        {
            var q = ctx.Request.Query;
            var mode = q["hub.mode"].ToString();
            var token = q["hub.verify_token"].ToString();
            var challenge = q["hub.challenge"].ToString();

            if (mode == "subscribe" && token == options.Value.VerifyToken && !string.IsNullOrEmpty(challenge))
                return Results.Text(challenge, "text/plain");

            return Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        // POST: signed inbound events. Validate signature over the raw body, enqueue, ACK fast.
        app.MapPost("/webhook", async (
            HttpContext ctx,
            IOptions<WhatsAppOptions> options,
            IInboundQueue queue,
            JobMatchDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("WhatsAppWebhook");

            byte[] raw;
            using (var ms = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(ms, ct);
                raw = ms.ToArray();
            }

            var signature = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!WhatsAppSignatureValidator.IsValid(options.Value.AppSecret, raw, signature))
            {
                logger.LogWarning("Rejected webhook POST: invalid signature.");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            try
            {
                var messages = WhatsAppPayloadParser.Parse(raw);
                foreach (var message in messages)
                {
                    if (!queue.TryEnqueue(message))
                        logger.LogWarning("Inbound queue full; dropped {MessageId} (Meta will retry).",
                            message.WaMessageId);
                }
            }
            catch (Exception ex)
            {
                // Never fail the ACK on a parse error — log and move on (Meta would retry endlessly).
                logger.LogError(ex, "Failed to parse webhook payload.");
            }

            // Delivery statuses for our replies: a failure is only ever reported here.
            try
            {
                await RecordStatusesAsync(WhatsAppPayloadParser.ParseStatuses(raw), db, logger, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to record delivery statuses.");
            }

            // Immediate 200 ACK (< 2s); all real work is async on the background consumer.
            return Results.Ok();
        });

        return app;
    }

    private static readonly string[] StatusOrder = { "sent", "delivered", "read" };

    /// <summary>
    /// Store each status on its outbound MessageLog row, and log every failure as a warning
    /// (Event Viewer) with Meta's reason. Statuses can arrive out of order; "read" is never
    /// downgraded to "delivered", and "failed" always wins.
    /// </summary>
    public static async Task RecordStatusesAsync(IReadOnlyList<DeliveryStatus> statuses, JobMatchDbContext db,
        ILogger logger, CancellationToken ct)
    {
        if (statuses.Count == 0) return;
        foreach (var s in statuses.Where(s => s.Status == "failed"))
            logger.LogWarning("WhatsApp could not deliver message {MessageId} to ...{Phone4}: {Error}",
                s.MessageId, s.RecipientWaId is { Length: >= 4 } r ? r[^4..] : "?", s.Error ?? "no reason given");

        var ids = statuses.Select(s => s.MessageId).Distinct().ToList();
        var rows = await db.MessageLogs.Where(m => m.WaMessageId != null && ids.Contains(m.WaMessageId)).ToListAsync(ct);
        foreach (var s in statuses)
        {
            var row = rows.FirstOrDefault(m => m.WaMessageId == s.MessageId);
            if (row is null || row.DeliveryStatus == "failed") continue;
            if (s.Status != "failed" && Array.IndexOf(StatusOrder, s.Status) <= Array.IndexOf(StatusOrder, row.DeliveryStatus))
                continue;
            row.DeliveryStatus = s.Status;
            row.DeliveryError = s.Error is { Length: > 400 } e ? e[..400] : s.Error;
            row.DeliveryUpdatedUtc = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
