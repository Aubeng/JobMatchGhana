using FluentAssertions;
using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
using JobMatch.Domain.Enums;
using JobMatch.Infrastructure.Configuration;
using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Matching;
using JobMatch.Infrastructure.Nlu;
using JobMatch.Infrastructure.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobMatch.Tests;

public class OrchestratorTests
{
    // One in-memory DB + session store + fake WhatsApp across turns, with a fresh DbContext per
    // turn (mirroring the scoped-per-message production wiring). NLU is the offline matcher.
    private sealed class Harness
    {
        private readonly string _dbName = "orch-" + Guid.NewGuid();
        public InMemorySessionStore Sessions { get; } = new();
        public FakeWhatsAppClient WhatsApp { get; } = new();
        public INluClient Nlu { get; } = new StubNluClient(TestCatalog.Instance);
        private int _seq;

        public JobMatchDbContext NewContext() =>
            new(new DbContextOptionsBuilder<JobMatchDbContext>().UseInMemoryDatabase(_dbName).Options);

        public async Task SendAsync(string waId, string text, string? messageId = null, string type = "text")
        {
            await using var db = NewContext();
            var matching = new MatchingService(db, new OfflineMatchRanker(), TestCatalog.Instance,
                Microsoft.Extensions.Options.Options.Create(new MatchingOptions()), NullLogger<MatchingService>.Instance);
            var orch = new FsmConversationOrchestrator(db, Sessions, WhatsApp, Nlu, TestCatalog.Instance, matching,
                NullLogger<FsmConversationOrchestrator>.Instance);
            await orch.HandleAsync(new InboundMessage(waId, messageId ?? $"wamid.{waId}.{++_seq}", text, type, null,
                DateTimeOffset.UtcNow), CancellationToken.None);
        }

        public Task TapAsync(string waId, string id, string? messageId = null) => SendAsync(waId, id, messageId, "interactive");

        public async Task SeedJobsAsync()
        {
            await using var db = NewContext();
            await new JobSeeder(db, TestCatalog.Instance).SeedAsync(
                Path.Combine(AppContext.BaseDirectory, "Seed", "jobs_synthetic_v1.json"), default);
        }

        /// <summary>Consent, Construction, two skills, 1-5 years, full-time, Madina; summary shown.</summary>
        public async Task ToSummaryAsync(string wa)
        {
            await SendAsync(wa, "hi");
            await TapAsync(wa, "1");
            await TapAsync(wa, "1");
            await SendAsync(wa, "i can lay blocks and do tiling");
            await TapAsync(wa, "done");
            await TapAsync(wa, "exp_1to5");
            await TapAsync(wa, "1");
            await TapAsync(wa, "2");
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
        (await db.MessageLogs.CountAsync(m => m.WaMessageId == "wamid.DUPE" && m.Direction == MessageDirection.Inbound))
            .Should().Be(1);
        h.WhatsApp.Sends.Should().HaveCount(1);
    }

    [Fact]
    public async Task No_worker_row_written_before_consent()
    {
        var h = new Harness();
        const string wa = "233240000002";
        await h.SendAsync(wa, "hi");

        await using var db = h.NewContext();
        (await db.Workers.AnyAsync(w => w.WaId == wa)).Should().BeFalse();
    }

    [Fact]
    public async Task Profile_is_written_only_when_the_summary_is_confirmed_FR10()
    {
        var h = new Harness();
        const string wa = "233240000003";
        await h.ToSummaryAsync(wa);

        await using (var before = h.NewContext())
        {
            var w = await before.Workers.SingleAsync(x => x.WaId == wa);
            w.ConsentGiven.Should().BeTrue();
            w.ConsentUtc.Should().NotBeNull();
            w.TradeCategoryId.Should().BeNull("not confirmed yet");
            (await before.WorkerSkills.CountAsync()).Should().Be(0);
        }

        await h.TapAsync(wa, "1");   // Yes, that's right

        await using var db = h.NewContext();
        var worker = await db.Workers.Include(x => x.Skills).ThenInclude(s => s.Skill).SingleAsync(x => x.WaId == wa);
        worker.TradeCategoryId.Should().Be("construction");
        worker.ExperienceYears.Should().Be(3);
        worker.Availability.Should().Be("Full-time");
        worker.LocationDistrict.Should().Be("La Nkwantanang Madina");
        worker.LocationText.Should().Be("Madina");
        worker.ProfileConfirmedUtc.Should().NotBeNull();
        worker.Skills.Select(s => s.Skill!.CanonicalName).Should().BeEquivalentTo("Block laying", "Tiling");
        worker.Skills.Should().OnlyContain(s => s.SourceUtterance == "i can lay blocks and do tiling", "research data preserved");
        worker.Skills.First().Skill!.Category.Should().Be("Construction & Building");
    }

    [Fact]
    public async Task Confirming_the_profile_delivers_matches_and_S7_answers_are_stored()
    {
        var h = new Harness();
        await h.SeedJobsAsync();
        const string wa = "233240000010";
        await h.ToSummaryAsync(wa);

        await h.TapAsync(wa, "1");                       // confirm -> S6
        var sent = h.WhatsApp.Sends.Select(x => x.Text).ToList();
        sent.Should().Contain(t => t.Contains("Block layer for 2-storey house") && t.Contains("GHS 180–250 per day"));
        sent.Last().Should().Contain("How helpful");

        await h.TapAsync(wa, "int_1");                    // interested in the first job
        await h.TapAsync(wa, "rate_4");
        await h.SendAsync(wa, "good jobs thank you");

        await using var db = h.NewContext();
        var matches = await db.Matches.Include(m => m.JobPosting).OrderBy(m => m.Rank).ToListAsync();
        matches.Should().NotBeEmpty();
        matches.Should().OnlyContain(m => m.Rating == 4 && m.FeedbackText == "good jobs thank you" && m.Channel == "conversation");
        matches[0].Status.Should().Be(MatchStatus.Interested);
        matches[0].InterestedUtc.Should().NotBeNull();
        (await h.Sessions.GetAsync(wa, default))!.State.Should().Be(ConversationState.Active);
    }

    [Fact]
    public async Task With_no_jobs_nearby_the_worker_is_told_so()
    {
        var h = new Harness();   // no jobs seeded
        const string wa = "233240000011";
        await h.ToSummaryAsync(wa);
        await h.TapAsync(wa, "1");
        h.WhatsApp.Sends.Last().Text.Should().Contain("no jobs");
    }

    [Fact]
    public async Task Reconfirming_with_fewer_skills_marks_the_dropped_ones_removed_not_deleted()
    {
        var h = new Harness();
        const string wa = "233240000004";
        await h.ToSummaryAsync(wa);
        await h.TapAsync(wa, "1");

        await h.SendAsync(wa, "update");
        await h.TapAsync(wa, "2");          // My skills
        await h.TapAsync(wa, "4");          // Tiling only
        await h.TapAsync(wa, "done");
        await h.TapAsync(wa, "1");          // confirm

        await using var db = h.NewContext();
        var skills = await db.WorkerSkills.Include(s => s.Skill).ToListAsync();
        skills.Should().HaveCount(2);
        skills.Single(s => s.Skill!.CanonicalName == "Block laying").RemovedUtc.Should().NotBeNull();
        skills.Single(s => s.Skill!.CanonicalName == "Tiling").RemovedUtc.Should().BeNull();
    }

    [Fact]
    public async Task Nlu_telemetry_is_logged_for_free_text_after_consent_only()
    {
        var h = new Harness();
        const string wa = "233240000005";

        await h.SendAsync(wa, "hi", "wamid.T1");                 // pre-consent
        await h.SendAsync(wa, "yes", "wamid.T2");                // the consent turn itself
        await h.TapAsync(wa, "1", "wamid.T3");                   // a tap
        await h.SendAsync(wa, "i can lay blocks", "wamid.T4");   // free text after consent

        await using var db = h.NewContext();
        var logs = await db.MessageLogs.Where(m => m.Direction == MessageDirection.Inbound).ToDictionaryAsync(m => m.WaMessageId!);
        logs["wamid.T1"].NluModelVersion.Should().BeNull();
        logs["wamid.T2"].NluModelVersion.Should().BeNull();
        logs["wamid.T3"].NluModelVersion.Should().BeNull();
        logs["wamid.T4"].NluModelVersion.Should().Be(StubNluClient.ModelVersion);
        logs["wamid.T4"].IntentDetected.Should().Be(Intent.ProvideSkill);
        logs["wamid.T4"].SessionState.Should().Be(ConversationState.ElicitSkills);
    }

    [Fact]
    public async Task Delete_my_data_removes_profile_messages_and_session_FR16()
    {
        var h = new Harness();
        const string wa = "233240000006";
        const string other = "233240000099";
        await h.ToSummaryAsync(wa);
        await h.TapAsync(wa, "1");
        await h.SendAsync(other, "hi");

        await h.SendAsync(wa, "delete my data");
        await h.TapAsync(wa, "1");

        await using var db = h.NewContext();
        (await db.Workers.AnyAsync(w => w.WaId == wa)).Should().BeFalse();
        (await db.WorkerSkills.AnyAsync()).Should().BeFalse();
        (await db.MessageLogs.AnyAsync(m => m.WaId == wa)).Should().BeFalse("interaction history too");
        (await db.MessageLogs.AnyAsync(m => m.WaId == other)).Should().BeTrue("only that worker's data");
        h.Sessions.Contains(wa).Should().BeFalse();
        h.WhatsApp.Sends.Last().Text.Should().Contain("deleted");
    }

    [Fact]
    public async Task A_session_in_a_retired_state_restarts_the_new_flow()
    {
        var h = new Harness();
        const string wa = "233240000007";
        h.Sessions.SeedRaw(wa, $$"""{"waId":"{{wa}}","state":5,"consentGiven":true}""");   // Slice 1 SkillsElicitation

        await h.SendAsync(wa, "hello");

        h.WhatsApp.Sends.Single().Text.Should().Contain("consent");
        (await h.Sessions.GetAsync(wa, default))!.State.Should().Be(ConversationState.Welcome);
    }

    [Fact]
    public async Task Opt_out_marks_worker_inactive()
    {
        var h = new Harness();
        const string wa = "233240000008";
        await h.SendAsync(wa, "hi");
        await h.TapAsync(wa, "1");
        await h.SendAsync(wa, "stop");

        await using var db = h.NewContext();
        (await db.Workers.SingleAsync(w => w.WaId == wa)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Handoff_and_outbound_options_are_logged()
    {
        var h = new Harness();
        const string wa = "233240000009";
        await h.SendAsync(wa, "hi");
        await h.TapAsync(wa, "1");
        foreach (var junk in new[] { "xyz", "qqq", "zzz" })
            await h.SendAsync(wa, junk);

        await using var db = h.NewContext();
        var inbound = await db.MessageLogs.Where(m => m.Direction == MessageDirection.Inbound).OrderBy(m => m.Id).ToListAsync();
        inbound.Last().IntentDetected.Should().Be(Intent.HumanHandoff);
        inbound.TakeLast(3).Should().OnlyContain(m => m.IsRepair);

        var outbound = await db.MessageLogs.Where(m => m.Direction == MessageDirection.Outbound).ToListAsync();
        outbound.Should().Contain(m => m.MessageType == "interactive" && m.Body!.Contains("[list:"));
        outbound.Should().OnlyContain(m => m.Body!.Length > 0);
    }
}
