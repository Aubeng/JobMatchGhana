using JobMatch.Domain.Abstractions;
using JobMatch.Infrastructure.Configuration;
using JobMatch.Infrastructure.Data;
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

        // --- NLU client: stub (Slice 1) or real FastAPI client (Slice 2) ---
        var nlu = config.GetSection(NluOptions.SectionName).Get<NluOptions>() ?? new NluOptions();
        if (nlu.UseStub)
        {
            services.AddSingleton<INluClient, StubNluClient>();
        }
        else
        {
            services.AddHttpClient<INluClient, HttpNluClient>((sp, http) =>
            {
                var opt = sp.GetRequiredService<IOptions<NluOptions>>().Value;
                http.BaseAddress = new Uri(opt.BaseUrl);
            });
        }

        // --- Orchestrator (scoped: owns a DbContext per message) ---
        services.AddScoped<IConversationOrchestrator, FsmConversationOrchestrator>();

        return services;
    }
}
