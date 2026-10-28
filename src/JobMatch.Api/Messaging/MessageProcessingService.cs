using JobMatch.Domain.Abstractions;

namespace JobMatch.Api.Messaging;

/// <summary>
/// Background consumer that drains <see cref="IInboundQueue"/> and processes each message
/// through a scoped <see cref="IConversationOrchestrator"/>. A fresh DI scope per message
/// gives each turn its own DbContext. Failures are logged, the worker gets an apology asking
/// them to resend (Meta won't: the webhook was already acknowledged), and the queue carries on.
/// </summary>
public class MessageProcessingService : BackgroundService
{
    private readonly IInboundQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessageProcessingService> _logger;

    public MessageProcessingService(
        IInboundQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<MessageProcessingService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Message processing service started.");
        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IConversationOrchestrator>();
                await orchestrator.HandleAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process message {MessageId} from {WaId}",
                    message.WaMessageId, message.WaId);
                await ApologiseAsync(message.WaId, stoppingToken);
            }
        }
        _logger.LogInformation("Message processing service stopping.");
    }

    /// <summary>
    /// The webhook has already been acknowledged, so Meta will not resend a message that failed
    /// here: tell the worker rather than leave them without a reply.
    /// </summary>
    private async Task ApologiseAsync(string waId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IWhatsAppClient>().SendAsync(waId,
                new JobMatch.Domain.Conversation.OutboundMessage(
                    "Sorry, something went wrong on our side 🙏 Please send your message again."), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not send the error apology to {WaId}.", waId);
        }
    }
}
