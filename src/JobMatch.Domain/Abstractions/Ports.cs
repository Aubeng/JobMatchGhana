using JobMatch.Domain.Conversation;

namespace JobMatch.Domain.Abstractions;

/// <summary>
/// Processes a single inbound message end-to-end: load session, run the FSM,
/// persist, send outbound, and log. Phase 1 shipped an echo implementation;
/// Slice 1 replaces it with the deterministic FSM orchestrator.
/// </summary>
public interface IConversationOrchestrator
{
    Task HandleAsync(InboundMessage message, CancellationToken ct);
}

/// <summary>Conversation session persistence (Redis in production, in-memory in tests).</summary>
public interface ISessionStore
{
    Task<ConversationSession?> GetAsync(string waId, CancellationToken ct);
    Task SaveAsync(ConversationSession session, CancellationToken ct);
    Task DeleteAsync(string waId, CancellationToken ct);
}

/// <summary>Typed WhatsApp Cloud API client (send text, mark-as-read).</summary>
public interface IWhatsAppClient
{
    Task<string?> SendTextAsync(string waId, string text, CancellationToken ct);
    Task MarkReadAsync(string waMessageId, CancellationToken ct);
}

/// <summary>
/// HTTP contract with the Python NLP microservice. Stubbed in Slice 1;
/// wired to FastAPI in Slice 2.
/// </summary>
public interface INluClient
{
    /// <summary>POST /nlu: utterance + FSM state -> intent + skill candidates.</summary>
    Task<NluResult> AnalyzeAsync(string utterance, string state, CancellationToken ct);
}
