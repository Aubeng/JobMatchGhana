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
    /// <summary>
    /// Force the logging (dry-run) client even when an access token is configured: nothing is
    /// sent to Meta. Use for demos, previews and any environment pointed at test data.
    /// </summary>
    public bool DryRun { get; set; }
    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com";
    public string GraphApiVersion { get; set; } = "v21.0";
}

/// <summary>Redis connection + session policy.</summary>
public class RedisOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
    /// <summary>
    /// Conversation session TTL. FR-09: an unfinished profile survives at least 72h, so a
    /// worker can resume without re-entering anything (sliding: refreshed on every message).
    /// </summary>
    public int SessionTtlHours { get; set; } = 72;
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
}

/// <summary>Matching engine (S&D L5) and new-job alerts.</summary>
public class MatchingOptions
{
    public const string SectionName = "Matching";

    /// <summary>S&D: return the top 3.</summary>
    public int TopK { get; set; } = 3;
    /// <summary>Minimum cosine similarity (MiniLM) for a job to be offered. From the build brief.</summary>
    public double MinScore { get; set; } = 0.55;
    /// <summary>At most one new-job alert per worker in this many hours (cost discipline).</summary>
    public int AlertCooldownHours { get; set; } = 24;
    /// <summary>Approved utility template for new-job alerts: "{{1}} in {{2}}" + quick reply.</summary>
    public string AlertTemplate { get; set; } = "job_match_alert";
    public string AlertTemplateLanguage { get; set; } = "en";
}

/// <summary>Where the dialogue's reference data (taxonomy, localities, districts) is read from.</summary>
public class DialogueOptions
{
    public const string SectionName = "Dialogue";

    /// <summary>Folder holding taxonomy_v1.json, localities_v1.json and ghana_districts.json.
    /// Relative paths resolve against the app's base directory. Default: the copies the API
    /// build places in <c>Data/</c>.</summary>
    public string DataPath { get; set; } = "Data";

    public string ResolvedDataPath => Path.IsPathRooted(DataPath) ? DataPath : Path.Combine(AppContext.BaseDirectory, DataPath);
}
