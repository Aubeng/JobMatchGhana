using System.Collections.Concurrent;
using System.Text.Json;
using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
using JobMatch.Infrastructure.Dialogue;

namespace JobMatch.Tests;

/// <summary>
/// In-memory <see cref="ISessionStore"/> for tests (no Redis). Sessions round-trip through
/// JSON exactly as <c>RedisSessionStore</c> stores them, so a field that wouldn't survive Redis
/// doesn't survive here either.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, string> _store = new();

    public Task<ConversationSession?> GetAsync(string waId, CancellationToken ct)
        => Task.FromResult(_store.TryGetValue(waId, out var s) ? JsonSerializer.Deserialize<ConversationSession>(s, Json) : null);

    public Task SaveAsync(ConversationSession session, CancellationToken ct)
    {
        _store[session.WaId] = JsonSerializer.Serialize(session, Json);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string waId, CancellationToken ct)
    {
        _store.TryRemove(waId, out _);
        return Task.CompletedTask;
    }

    /// <summary>Put a raw JSON session in place (e.g. one from before a schema change).</summary>
    public void SeedRaw(string waId, string json) => _store[waId] = json;

    public bool Contains(string waId) => _store.ContainsKey(waId);
}

/// <summary>Fake WhatsApp client that records every outbound send (a "faked Graph API").</summary>
public sealed class FakeWhatsAppClient : IWhatsAppClient
{
    public record Sent(string WaId, OutboundMessage Message)
    {
        public string Text => Message.Text;
    }

    public ConcurrentQueue<Sent> Sends { get; } = new();
    public ConcurrentQueue<string> Reads { get; } = new();
    private int _counter;

    public Task<string?> SendAsync(string waId, OutboundMessage message, CancellationToken ct)
    {
        Sends.Enqueue(new Sent(waId, message));
        return Task.FromResult<string?>($"fake-out-{Interlocked.Increment(ref _counter)}");
    }

    public Task MarkReadAsync(string waMessageId, CancellationToken ct)
    {
        Reads.Enqueue(waMessageId);
        return Task.CompletedTask;
    }
}

/// <summary>The real dialogue catalog (nlp/data, copied to the test output's Data/).</summary>
public static class TestCatalog
{
    public static DialogueCatalog Instance { get; } =
        DialogueCatalogLoader.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
}
