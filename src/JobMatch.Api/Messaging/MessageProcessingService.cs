using JobMatch.Domain.Abstractions;

namespace JobMatch.Api.Messaging;

/// <summary>
/// Background consumer that drains <see cref="IInboundQueue"/> and processes each message
/// through a scoped <see cref="IConversationOrchestrator"/>. A fresh DI scope per message
/// gives each turn its own DbContext. Failures are logged and swallowed so one bad message
/// never stalls the queue (Meta will redeliver; idempotency dedupes).
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
            }
        }
        _logger.LogInformation("Message processing service stopping.");
    }
}
