using System.Text;
using FluentAssertions;
using JobMatch.Api.WhatsApp;
using JobMatch.Domain.Entities;
using JobMatch.Domain.Enums;
using JobMatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobMatch.Tests;

/// <summary>Meta's status callbacks: the only place a failed delivery is reported.</summary>
public class DeliveryStatusTests
{
    private static byte[] Statuses(params string[] statuses) => Encoding.UTF8.GetBytes(
        "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"id\":\"1\",\"changes\":[{\"field\":\"messages\",\"value\":{" +
        "\"messaging_product\":\"whatsapp\",\"metadata\":{\"phone_number_id\":\"1\"},\"statuses\":[" +
        string.Join(",", statuses) + "]}}]}]}");

    private const string Failed =
        "{\"id\":\"wamid.OUT1\",\"status\":\"failed\",\"timestamp\":\"1761200000\",\"recipient_id\":\"233241234567\"," +
        "\"errors\":[{\"code\":131026,\"title\":\"Message undeliverable\",\"error_data\":{\"details\":\"Recipient not on WhatsApp\"}}]}";

    [Fact]
    public void Statuses_are_parsed_with_metas_reason_and_messages_are_not()
    {
        var parsed = WhatsAppPayloadParser.ParseStatuses(Statuses(Failed,
            "{\"id\":\"wamid.OUT2\",\"status\":\"read\",\"timestamp\":\"1761200001\",\"recipient_id\":\"233241234567\"}"));

        parsed.Should().HaveCount(2);
        parsed[0].Should().Be(new DeliveryStatus("wamid.OUT1", "failed", "233241234567",
            "131026 | Message undeliverable | Recipient not on WhatsApp"));
        parsed[1].Error.Should().BeNull();
        WhatsAppPayloadParser.Parse(Statuses(Failed)).Should().BeEmpty("a status is not an inbound message");
    }

    [Fact]
    public async Task Statuses_are_stored_on_the_outbound_row_without_going_backwards()
    {
        await using var db = new JobMatchDbContext(new DbContextOptionsBuilder<JobMatchDbContext>()
            .UseInMemoryDatabase("status-" + Guid.NewGuid()).Options);
        db.MessageLogs.AddRange(
            new MessageLog { WaMessageId = "wamid.OUT1", WaId = "233241234567", Direction = MessageDirection.Outbound, Body = "hi" },
            new MessageLog { WaMessageId = "wamid.OUT2", WaId = "233241234567", Direction = MessageDirection.Outbound, Body = "hi" });
        await db.SaveChangesAsync();

        var read = new DeliveryStatus("wamid.OUT2", "read", null, null);
        var lateDelivered = new DeliveryStatus("wamid.OUT2", "delivered", null, null);
        var failed = WhatsAppPayloadParser.ParseStatuses(Statuses(Failed)).Single();
        await WebhookEndpoints.RecordStatusesAsync(new[] { read, lateDelivered, failed }, db, NullLogger.Instance, default);

        var rows = await db.MessageLogs.ToDictionaryAsync(m => m.WaMessageId!);
        rows["wamid.OUT1"].DeliveryStatus.Should().Be("failed");
        rows["wamid.OUT1"].DeliveryError.Should().StartWith("131026");
        rows["wamid.OUT2"].DeliveryStatus.Should().Be("read", "a late 'delivered' doesn't undo 'read'");
    }
}
