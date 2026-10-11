using FluentAssertions;
using JobMatch.Domain.Conversation;
using JobMatch.Domain.Enums;
using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Nlu;
using JobMatch.Infrastructure.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobMatch.Tests;

public class OrchestratorTests
{
    // A harness sharing one in-memory DB + session store + fake WhatsApp across turns,
    // building a fresh DbContext per turn (mirroring the scoped-per-message production wiring).
    private sealed class Harness
    {
        private readonly string _dbName = "orch-" + Guid.NewGuid();
        public InMemorySessionStore Sessions { get; } = new();
        public FakeWhatsAppClient WhatsApp { get; } = new();
        public StubNluClient Nlu { get; } = new();
        private int _seq;

        public JobMatchDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<JobMatchDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            return new JobMatchDbContext(options);
        }

        public async Task SendAsync(string waId, string text, string? messageId = null)
        {
            await using var db = NewContext();
            var orch = new FsmConversationOrchestrator(
                db, Sessions, WhatsApp, Nlu, NullLogger<FsmConversationOrchestrator>.Instance);
            var msg = new InboundMessage(
                waId, messageId ?? $"wamid.{waId}.{++_seq}", text, "text", null, DateTimeOffset.UtcNow);
            await orch.HandleAsync(msg, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Duplicate_message_id_is_processed_once()
    {
        var h = new Harness();
        const string wa = "233240000001";

        await h.SendAsync(wa, "hi", "wamid.DUPE");
        await h.SendAsync(wa, "hi", "wamid.DUPE"); // redelivery

        await using var db = h.NewContext();
        var inboundCount = await db.MessageLogs
            .CountAsync(m => m.WaMessageId == "wamid.DUPE" && m.Direction == MessageDirection.Inbound);

        inboundCount.Should().Be(1);
        h.WhatsApp.Sends.Should().HaveCount(1); // only the first turn produced a reply
    }

    [Fact]
    public async Task No_worker_row_written_before_consent()
    {
        var h = new Harness();
        const string wa = "233240000002";

        await h.SendAsync(wa, "hi"); // New -> Consent, no consent yet

        await using var db = h.NewContext();
        (await db.Workers.AnyAsync(w => w.WaId == wa)).Should().BeFalse();
    }

    [Fact]
    public async Task End_to_end_flow_persists_worker_and_research_fields()
    {
        var h = new Harness();
        const string wa = "233240000003";

        await h.SendAsync(wa, "hi");                       // -> Consent
        await h.SendAsync(wa, "1");                        // consent yes -> Language
        await h.SendAsync(wa, "1");                        // English -> Name
        await h.SendAsync(wa, "Ama Mensah");               // -> Location
        await h.SendAsync(wa, "Kumasi");                   // -> SkillsElicitation
        await h.SendAsync(wa, "I can lay blocks");         // NLU (stub) -> Masonry, pending confirm
        await h.SendAsync(wa, "yes");                      // confirm skill
        await h.SendAsync(wa, "done");                     // -> Availability
        await h.SendAsync(wa, "2");                        // Part-time -> Active

        await using var db = h.NewContext();
        var worker = await db.Workers
            .Include(w => w.Skills).ThenInclude(s => s.Skill)
            .SingleAsync(w => w.WaId == wa);

        worker.ConsentGiven.Should().BeTrue();
        worker.ConsentUtc.Should().NotBeNull();
        worker.DisplayName.Should().Be("Ama Mensah");
        worker.LocationText.Should().Be("Kumasi");
        worker.IsActive.Should().BeTrue();

        worker.Skills.Should().HaveCount(1);
        var ws = worker.Skills.Single();
        ws.Skill!.CanonicalName.Should().Be("Masonry");
        ws.SourceUtterance.Should().Be("I can lay blocks"); // research data preserved
        ws.ExtractionConfidence.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Nlu_telemetry_is_logged_after_consent_and_never_before()
    {
        var h = new Harness();
        const string wa = "233240000005";

        await h.SendAsync(wa, "hi", "wamid.T1");                 // pre-consent: not analysed
        await h.SendAsync(wa, "1", "wamid.T2");                  // the consent turn itself: not analysed
        await h.SendAsync(wa, "1", "wamid.T3");                  // menu digit: not analysed
        await h.SendAsync(wa, "Kofi", "wamid.T4");               // post-consent free text: analysed
        await h.SendAsync(wa, "Madina", "wamid.T5");
        await h.SendAsync(wa, "I can lay blocks", "wamid.T6");

        await using var db = h.NewContext();
        var logs = await db.MessageLogs.Where(m => m.Direction == MessageDirection.Inbound)
            .ToDictionaryAsync(m => m.WaMessageId!);

        logs["wamid.T1"].NluModelVersion.Should().BeNull();
        logs["wamid.T2"].NluModelVersion.Should().BeNull();
        logs["wamid.T3"].NluModelVersion.Should().BeNull();
        logs["wamid.T4"].NluModelVersion.Should().Be(StubNluClient.ModelVersion);

        var skillTurn = logs["wamid.T6"];
        skillTurn.NluIntent.Should().Be("provide_skill");
        skillTurn.NluIntentConfidence.Should().BeGreaterThan(0);
        skillTurn.IntentDetected.Should().Be(Intent.ProvideSkill);  // the FSM's own decision, kept separately
    }

    [Fact]
    public async Task Opt_out_marks_worker_inactive()
    {
        var h = new Harness();
        const string wa = "233240000004";

        await h.SendAsync(wa, "hi");
        await h.SendAsync(wa, "1");   // consent
        await h.SendAsync(wa, "stop"); // opt out

        await using var db = h.NewContext();
        var worker = await db.Workers.SingleAsync(w => w.WaId == wa);
        worker.IsActive.Should().BeFalse();
    }
}
