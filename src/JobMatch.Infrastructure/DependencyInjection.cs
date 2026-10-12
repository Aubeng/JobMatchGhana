using JobMatch.Domain.Abstractions;
using JobMatch.Infrastructure.Configuration;
using JobMatch.Domain.Conversation;
using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Dialogue;
using JobMatch.Infrastructure.Matching;
using JobMatch.Domain.Matching;
using JobMatch.Infrastructure.Nlu;
using JobMatch.Infrastructure.Orchestration;
using JobMatch.Infrastructure.Sessions;
using JobMatch.Infrastructure.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace JobMatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddJobMatchInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<WhatsAppOptions>(config.GetSection(WhatsAppOptions.SectionName));
        services.Configure<RedisOptions>(config.GetSection(RedisOptions.SectionName));
        services.Configure<NluOptions>(config.GetSection(NluOptions.SectionName));

        // --- SQL Server (EF Core, code-first) ---
        var connectionString = config.GetConnectionString("SqlServer")
            ?? "Server=localhost;Database=JobMatchGhana;Trusted_Connection=True;TrustServerCertificate=True";
        services.AddDbContext<JobMatchDbContext>(o => o.UseSqlServer(connectionString));

        // --- Redis session store ---
        var redisOptions = config.GetSection(RedisOptions.SectionName).Get<RedisOptions>() ?? new RedisOptions();
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var cfg = ConfigurationOptions.Parse(redisOptions.ConnectionString);
            cfg.AbortOnConnectFail = false; // tolerate Redis being briefly unavailable at startup
            return ConnectionMultiplexer.Connect(cfg);
        });
        services.AddSingleton<ISessionStore, RedisSessionStore>();

        // --- WhatsApp client: real Graph client when credentials exist, else a logging stub ---
        var whatsapp = config.GetSection(WhatsAppOptions.SectionName).Get<WhatsAppOptions>() ?? new WhatsAppOptions();
        if (!string.IsNullOrWhiteSpace(whatsapp.AccessToken))
        {
            services.AddHttpClient<IWhatsAppClient, WhatsAppClient>((sp, http) =>
            {
                var opt = sp.GetRequiredService<IOptions<WhatsAppOptions>>().Value;
                http.BaseAddress = new Uri(opt.GraphBaseUrl);
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opt.AccessToken);
            });
        }
        else
        {
            services.AddSingleton<IWhatsAppClient, LoggingWhatsAppClient>();
        }

        // --- Dialogue reference data: taxonomy, menus, gazetteer (the files nlp/ resolves against) ---
        services.Configure<DialogueOptions>(config.GetSection(DialogueOptions.SectionName));
        services.AddSingleton(sp => DialogueCatalogLoader.Load(
            sp.GetRequiredService<IOptions<DialogueOptions>>().Value.ResolvedDataPath));

        // --- NLU client: offline taxonomy matcher, or the FastAPI service with that as fallback ---
        var nlu = config.GetSection(NluOptions.SectionName).Get<NluOptions>() ?? new NluOptions();
        if (nlu.UseStub)
        {
            services.AddSingleton<INluClient, StubNluClient>();
        }
        else
        {
            services.AddHttpClient<HttpNluClient>((sp, http) =>
            {
                var opt = sp.GetRequiredService<IOptions<NluOptions>>().Value;
                http.BaseAddress = new Uri(opt.BaseUrl);
                http.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds);
            });
            // Service first; keyword stub if it's down or slow (graceful degradation).
            services.AddTransient<INluClient>(sp => new FallbackNluClient(
                sp.GetRequiredService<HttpNluClient>(),
                new StubNluClient(sp.GetRequiredService<DialogueCatalog>()),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FallbackNluClient>>()));
        }

        // --- Matching engine (S&D L5): MiniLM ranking in the Python service, skill overlap offline ---
        services.Configure<MatchingOptions>(config.GetSection(MatchingOptions.SectionName));
        if (nlu.UseStub)
        {
            services.AddSingleton<IMatchRanker, OfflineMatchRanker>();
        }
        else
        {
            services.AddHttpClient<HttpMatchRanker>((sp, http) =>
            {
                var opt = sp.GetRequiredService<IOptions<NluOptions>>().Value;
                http.BaseAddress = new Uri(opt.BaseUrl);
                http.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds);
            });
            services.AddTransient<IMatchRanker>(sp => new FallbackMatchRanker(
                sp.GetRequiredService<HttpMatchRanker>(),
                new OfflineMatchRanker(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FallbackMatchRanker>>()));
        }
        services.AddScoped<MatchingService>();
        services.AddScoped<JobAlertService>();
        services.AddScoped<JobSeeder>();

        // --- Orchestrator (scoped: owns a DbContext per message) ---
        services.AddScoped<IConversationOrchestrator, FsmConversationOrchestrator>();

        return services;
    }
}
