using System.Collections.Concurrent;
using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;

namespace JobMatch.Tests;

/// <summary>In-memory <see cref="ISessionStore"/> for tests (no Redis).</summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, ConversationSession> _store = new();

    public Task<ConversationSession?> GetAsync(string waId, CancellationToken ct)
        => Task.FromResult(_store.TryGetValue(waId, out var s) ? Clone(s) : null);

    public Task SaveAsync(ConversationSession session, CancellationToken ct)
    {
        _store[session.WaId] = Clone(session);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string waId, CancellationToken ct)
    {
        _store.TryRemove(waId, out _);
        return Task.CompletedTask;
    }

    // Deep-ish clone so callers can't mutate stored state by reference (mirrors Redis JSON round-trip).
    private static ConversationSession Clone(ConversationSession s) => new()
    {
        WaId = s.WaId,
        State = s.State,
        DisplayName = s.DisplayName,
        PreferredLanguage = s.PreferredLanguage,
        LocationText = s.LocationText,
        ConsentGiven = s.ConsentGiven,
        Availability = s.Availability,
        ConfirmedSkills = new List<string>(s.ConfirmedSkills),
        PendingSkill = s.PendingSkill is null ? null : new PendingSkill
        {
            CanonicalName = s.PendingSkill.CanonicalName,
            SourceUtterance = s.PendingSkill.SourceUtterance,
            Confidence = s.PendingSkill.Confidence
        },
        QueuedSkills = s.QueuedSkills.Select(q => new PendingSkill
        {
            CanonicalName = q.CanonicalName,
            SourceUtterance = q.SourceUtterance,
            Confidence = q.Confidence
        }).ToList(),
        RepairCount = s.RepairCount,
        CreatedUtc = s.CreatedUtc,
        UpdatedUtc = s.UpdatedUtc
    };
}

/// <summary>Fake WhatsApp client that records every outbound send (a "faked Graph API").</summary>
public sealed class FakeWhatsAppClient : IWhatsAppClient
{
    public record Sent(string WaId, string Text);

    public ConcurrentQueue<Sent> Sends { get; } = new();
    public ConcurrentQueue<string> Reads { get; } = new();
    private int _counter;

    public Task<string?> SendTextAsync(string waId, string text, CancellationToken ct)
    {
        Sends.Enqueue(new Sent(waId, text));
        return Task.FromResult<string?>($"fake-out-{Interlocked.Increment(ref _counter)}");
    }

    public Task MarkReadAsync(string waMessageId, CancellationToken ct)
    {
        Reads.Enqueue(waMessageId);
        return Task.CompletedTask;
    }
}
