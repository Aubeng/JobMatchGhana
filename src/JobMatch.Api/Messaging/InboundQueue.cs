using System.Threading.Channels;
using JobMatch.Domain.Conversation;

namespace JobMatch.Api.Messaging;

/// <summary>
/// Bounded in-process queue decoupling webhook ingestion from processing so the webhook
/// can ACK &lt; 2s. Backed by <see cref="System.Threading.Channels"/>. (DL-002)
/// </summary>
public interface IInboundQueue
{
    /// <summary>Try to enqueue without blocking. Returns false if the queue is full.</summary>
    bool TryEnqueue(InboundMessage message);
    ChannelReader<InboundMessage> Reader { get; }
}

public sealed class InboundQueue : IInboundQueue
{
    private readonly Channel<InboundMessage> _channel;

    public InboundQueue(int capacity = 1000)
    {
        _channel = Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public bool TryEnqueue(InboundMessage message) => _channel.Writer.TryWrite(message);

    public ChannelReader<InboundMessage> Reader => _channel.Reader;
}
