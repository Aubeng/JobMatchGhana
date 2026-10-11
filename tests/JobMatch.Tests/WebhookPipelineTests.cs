using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JobMatch.Domain.Abstractions;
using JobMatch.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using Xunit;

namespace JobMatch.Tests;

/// <summary>
/// Boots the real API host with SQL Server swapped for EF InMemory, Redis swapped for the
/// in-memory session store, and the WhatsApp Graph client faked — exercising the full
/// ingest -> validate -> queue -> background-consumer -> FSM pipeline.
/// </summary>
public class WebhookPipelineFactory : WebApplicationFactory<Program>
{
    public const string AppSecret = "integration-app-secret";
    public const string VerifyToken = "integration-verify-token";
    public FakeWhatsAppClient WhatsApp { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WhatsApp:AppSecret"] = AppSecret,
                ["WhatsApp:VerifyToken"] = VerifyToken,
                ["Nlu:UseStub"] = "true"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Swap SQL Server -> EF InMemory (single shared database).
            var dbName = "it-" + Guid.NewGuid();
            var remove = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<JobMatchDbContext>) ||
                d.ServiceType == typeof(JobMatchDbContext) ||
                (d.ServiceType.IsGenericType &&
                 d.ServiceType.GetGenericTypeDefinition().Name.StartsWith("IDbContextOptionsConfiguration")))
                .ToList();
            foreach (var d in remove) services.Remove(d);
            services.AddDbContext<JobMatchDbContext>(o => o.UseInMemoryDatabase(dbName));

            // Swap Redis -> in-memory session store; drop the Redis multiplexer entirely.
            services.RemoveAll<IConnectionMultiplexer>();
            services.RemoveAll<ISessionStore>();
            services.AddSingleton<ISessionStore, InMemorySessionStore>();

            // Fake the Graph API.
            services.RemoveAll<IWhatsAppClient>();
            services.AddSingleton<IWhatsAppClient>(WhatsApp);
        });
    }
}

public class WebhookPipelineTests : IClassFixture<WebhookPipelineFactory>
{
    private readonly WebhookPipelineFactory _factory;

    public WebhookPipelineTests(WebhookPipelineFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_verification_echoes_challenge_when_token_matches()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            $"/webhook?hub.mode=subscribe&hub.verify_token={WebhookPipelineFactory.VerifyToken}&hub.challenge=42");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("42");
    }

    [Fact]
    public async Task Get_verification_rejects_wrong_token()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=42");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_with_invalid_signature_is_rejected()
    {
        var client = _factory.CreateClient();
        var body = Payload("233260000000", "wamid.BAD", "hi");
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Hub-Signature-256", "sha256=deadbeef");

        var resp = await client.PostAsync("/webhook", content);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_with_valid_signature_is_acked_and_processed()
    {
        var client = _factory.CreateClient();
        const string wa = "233260000001";
        var body = Payload(wa, "wamid.OK1", "hello");
        var bytes = Encoding.UTF8.GetBytes(body);

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.Add("X-Hub-Signature-256", Sign(WebhookPipelineFactory.AppSecret, bytes));

        var resp = await client.PostAsync("/webhook", content);
        resp.StatusCode.Should().Be(HttpStatusCode.OK); // fast ACK

        // Background consumer processes asynchronously — wait for the welcome/consent reply.
        var sent = await WaitForSendAsync(wa);
        sent.Should().NotBeNull();
        sent!.Text.Should().Contain("consent");
    }

    private async Task<FakeWhatsAppClient.Sent?> WaitForSendAsync(string wa, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hit = _factory.WhatsApp.Sends.FirstOrDefault(s => s.WaId == wa);
            if (hit is not null) return hit;
            await Task.Delay(50);
        }
        return null;
    }

    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private static string Payload(string wa, string id, string text) => $$"""
    {
      "object": "whatsapp_business_account",
      "entry": [{
        "id": "0",
        "changes": [{
          "field": "messages",
          "value": {
            "messaging_product": "whatsapp",
            "metadata": { "display_phone_number": "233...", "phone_number_id": "PNID" },
            "contacts": [{ "profile": { "name": "Tester" }, "wa_id": "{{wa}}" }],
            "messages": [{
              "from": "{{wa}}",
              "id": "{{id}}",
              "timestamp": "1700000000",
              "type": "text",
              "text": { "body": "{{text}}" }
            }]
          }
        }]
      }]
    }
    """;
}
