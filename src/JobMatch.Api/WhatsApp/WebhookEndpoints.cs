using JobMatch.Api.Messaging;
using JobMatch.Infrastructure.Configuration;
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

            // Immediate 200 ACK (< 2s); all real work is async on the background consumer.
            return Results.Ok();
        });

        return app;
    }
}
