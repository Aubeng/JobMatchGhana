using JobMatch.Api.Messaging;
using JobMatch.Api.WhatsApp;
using JobMatch.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Infrastructure: EF Core (SQL Server), Redis sessions, WhatsApp + NLU clients, orchestrator.
builder.Services.AddJobMatchInfrastructure(builder.Configuration);

// Ingestion pipeline: bounded queue + background consumer (DL-002).
builder.Services.AddSingleton<IInboundQueue>(_ => new InboundQueue(capacity: 1000));
builder.Services.AddHostedService<MessageProcessingService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapWhatsAppWebhook();

app.Run();

// Exposed so the integration test host (WebApplicationFactory) can reference the entry point.
public partial class Program { }
