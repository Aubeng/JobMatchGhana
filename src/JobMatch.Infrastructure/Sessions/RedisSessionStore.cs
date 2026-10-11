using System.Text.Json;
using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
using JobMatch.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace JobMatch.Infrastructure.Sessions;

/// <summary>
/// Conversation session store backed by Redis. Sessions are JSON blobs keyed by
/// <c>{prefix}{wa_id}</c> with a sliding 24h TTL matching the WhatsApp service window.
/// </summary>
public class RedisSessionStore : ISessionStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOptions _options;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public RedisSessionStore(IConnectionMultiplexer redis, IOptions<RedisOptions> options)
    {
        _redis = redis;
        _options = options.Value;
    }

    private string Key(string waId) => _options.KeyPrefix + waId;

    public async Task<ConversationSession?> GetAsync(string waId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync(Key(waId));
        if (value.IsNullOrEmpty) return null;
        return JsonSerializer.Deserialize<ConversationSession>(value!, JsonOpts);
    }

    public async Task SaveAsync(ConversationSession session, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(session, JsonOpts);
        await db.StringSetAsync(Key(session.WaId), json, TimeSpan.FromHours(_options.SessionTtlHours));
    }

    public async Task DeleteAsync(string waId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync(Key(waId));
    }
}
