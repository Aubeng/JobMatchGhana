using JobMatch.Api;
using JobMatch.Api.Admin;
using JobMatch.Api.Messaging;
using JobMatch.Api.WhatsApp;
using JobMatch.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Infrastructure: EF Core (SQL Server), Redis sessions, WhatsApp + NLU clients, orchestrator.
builder.Services.AddJobMatchInfrastructure(builder.Configuration);

// Ingestion pipeline: bounded queue + background consumer (DL-002).
builder.Services.AddSingleton<IInboundQueue>(_ => new InboundQueue(capacity: 1000));
builder.Services.AddHostedService<MessageProcessingService>();

// Admin SPA sign-in (Identity, strict same-origin cookie).
builder.Services.AddAdminAuth(builder.Environment);

var app = builder.Build();

// Say plainly at startup whether this instance can message real people.
var waClient = app.Services.GetRequiredService<JobMatch.Domain.Abstractions.IWhatsAppClient>();
app.Logger.Log(waClient is JobMatch.Infrastructure.WhatsApp.LoggingWhatsAppClient ? LogLevel.Information : LogLevel.Warning,
    "WhatsApp client: {Client}", waClient is JobMatch.Infrastructure.WhatsApp.LoggingWhatsAppClient
        ? "DRY RUN (nothing is sent)" : "LIVE (messages go to real WhatsApp numbers)");

// Operator commands (seed-jobs, alert-job) run and exit instead of starting the web host.
if (await AdminCommands.TryRunAsync(app.Services, args))
    return;

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapWhatsAppWebhook();
app.MapAdminApi();

// The admin SPA (admin/, built into wwwroot/admin): static assets, and index.html for its routes.
app.UseStaticFiles();
app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");

app.Run();

// Exposed so the integration test host (WebApplicationFactory) can reference the entry point.
public partial class Program { }
