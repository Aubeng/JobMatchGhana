namespace JobMatch.Infrastructure.Configuration;

/// <summary>WhatsApp Cloud API / Meta Graph settings. Secrets come from user-secrets / App Service config.</summary>
public class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>App secret used to validate X-Hub-Signature-256 (HMAC-SHA256 over the raw body).</summary>
    public string AppSecret { get; set; } = string.Empty;
    /// <summary>Token echoed back on the GET verification handshake.</summary>
    public string VerifyToken { get; set; } = string.Empty;
    /// <summary>Business phone number id used as the sender.</summary>
    public string PhoneNumberId { get; set; } = string.Empty;
    /// <summary>Long-lived access token for Graph API calls.</summary>
    public string AccessToken { get; set; } = string.Empty;
    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com";
    public string GraphApiVersion { get; set; } = "v21.0";
}

/// <summary>Redis connection + session policy.</summary>
public class RedisOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
    /// <summary>Conversation session TTL. Aligns with WhatsApp's 24h service window.</summary>
    public int SessionTtlHours { get; set; } = 24;
    public string KeyPrefix { get; set; } = "jobmatch:session:";
}

/// <summary>Python NLP microservice settings.</summary>
public class NluOptions
{
    public const string SectionName = "Nlu";

    public string BaseUrl { get; set; } = "http://localhost:8000";
    /// <summary>When true, use the built-in stub instead of calling the service (Slice 1 default).</summary>
    public bool UseStub { get; set; } = true;
    /// <summary>Per-request timeout. On timeout or error the gateway falls back to the keyword stub.</summary>
    public double TimeoutSeconds { get; set; } = 5;
    public double MatchThreshold { get; set; } = 0.55;
}
