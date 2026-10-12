using System.Text.Json;
using FluentAssertions;
using JobMatch.Domain.Entities;
using JobMatch.Domain.Enums;
using JobMatch.Domain.Matching;
using JobMatch.Infrastructure.Configuration;
using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Matching;
using JobMatch.Infrastructure.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobMatch.Tests;

/// <summary>The S&D matching engine (L5): stage 1 filter, stage 2 ranking, delivery records, alerts.</summary>
public class MatchingTests
{
    private static readonly string SeedPath = Path.Combine(AppContext.BaseDirectory, "Seed", "jobs_synthetic_v1.json");
    private static readonly IOptions<MatchingOptions> Options = Microsoft.Extensions.Options.Options.Create(new MatchingOptions());

    /// <summary>Records what stage 2 was given; ranks in the given order with fixed scores.</summary>
    private sealed class RecordingRanker : IMatchRanker
    {
        public List<MatchCandidate> Seen { get; } = new();

        public Task<RankingResult> RankAsync(MatchProfile profile, IReadOnlyList<MatchCandidate> candidates, int topK,
            double minScore, CancellationToken ct)
        {
            Seen.AddRange(candidates);
            var ranked = candidates.Take(topK).Select((c, i) => new RankedMatch(c.JobId, i + 1, 0.9 - i * 0.1,
                profile.Skills.FirstOrDefault(), 0.8, c.RequiredSkills.Intersect(profile.Skills).ToList())).ToList();
            return Task.FromResult(new RankingResult("recording", "profile", ranked));
        }
    }

    private static async Task<JobMatchDbContext> SeededDbAsync()
    {
        var db = new JobMatchDbContext(new DbContextOptionsBuilder<JobMatchDbContext>()
            .UseInMemoryDatabase("match-" + Guid.NewGuid()).Options);
        await new JobSeeder(db, TestCatalog.Instance).SeedAsync(SeedPath, default);
        return db;
    }

    private static async Task<Worker> WorkerAsync(JobMatchDbContext db, string district, string availability,
        params string[] skills)
    {
        var worker = new Worker
        {
            WaId = "2332" + Random.Shared.Next(10000000, 99999999), ConsentGiven = true, IsActive = true,
            TradeName = "Construction & Building", LocationDistrict = district, Availability = availability,
            ProfileConfirmedUtc = DateTimeOffset.UtcNow
        };
        db.Workers.Add(worker);
        await db.SaveChangesAsync();
        foreach (var name in skills)
        {
            var skill = await db.Skills.FirstOrDefaultAsync(s => s.CanonicalName == name)
                        ?? db.Skills.Add(new Skill { CanonicalName = name }).Entity;
            db.WorkerSkills.Add(new WorkerSkill { WorkerId = worker.Id, Skill = skill, SourceUtterance = "test", ExtractionConfidence = 1 });
        }
        await db.SaveChangesAsync();
        return worker;
    }

    private static MatchingService Service(JobMatchDbContext db, IMatchRanker ranker)
        => new(db, ranker, TestCatalog.Instance, Options, NullLogger<MatchingService>.Instance);

    [Fact]
    public async Task Seeder_loads_the_synthetic_jobs_once_and_marks_them()
    {
        await using var db = await SeededDbAsync();
        (await db.JobPostings.CountAsync()).Should().Be(32);
        (await db.JobPostings.AllAsync(j => j.IsSynthetic && j.District != null && j.Status == "Open")).Should().BeTrue();
        (await new JobSeeder(db, TestCatalog.Instance).SeedAsync(SeedPath, default)).Added.Should().Be(0);
        var block = await db.JobPostings.Include(j => j.Skills).ThenInclude(s => s.Skill).SingleAsync(j => j.Title.StartsWith("Block layer"));
        block.District.Should().Be("La Nkwantanang Madina");
        block.Skills.Select(s => s.Skill!.CanonicalName).Should().BeEquivalentTo("Block laying", "Concrete work");
    }

    [Fact]
    public void Adjacency_is_symmetric_and_includes_the_district_itself()
    {
        var madina = TestCatalog.Instance.Neighbourhood("La Nkwantanang Madina");
        madina.Should().Contain(new[] { "La Nkwantanang Madina", "Adentan", "Ga East", "Ayawaso West" });
        madina.Should().NotContain("Tema");
        TestCatalog.Instance.Neighbourhood("Ga East").Should().Contain("La Nkwantanang Madina");
    }

    [Fact]
    public async Task Stage1_only_offers_jobs_in_the_workers_district_or_adjacent_ones()
    {
        await using var db = await SeededDbAsync();
        var worker = await WorkerAsync(db, "La Nkwantanang Madina", "Full-time", "Block laying");
        var ranker = new RecordingRanker();

        await Service(db, ranker).FindForWorkerAsync(worker, default);

        var districts = await db.JobPostings.Where(j => ranker.Seen.Select(c => c.JobId).Contains(j.Id))
            .Select(j => j.District!).Distinct().ToListAsync();
        districts.Should().NotBeEmpty();
        districts.Should().OnlyContain(d => TestCatalog.Instance.Neighbourhood("La Nkwantanang Madina").Contains(d));
        ranker.Seen.Should().NotContain(c => c.Title == "Night security guard", "Tema is not next to Madina");
    }

    [Fact]
    public async Task Stage1_respects_availability()
    {
        await using var db = await SeededDbAsync();
        var worker = await WorkerAsync(db, "Ledzokuku", "Weekends only", "Event catering");
        var ranker = new RecordingRanker();

        await Service(db, ranker).FindForWorkerAsync(worker, default);

        var offered = await db.JobPostings.Where(j => ranker.Seen.Select(c => c.JobId).Contains(j.Id)).ToListAsync();
        offered.Should().NotBeEmpty();
        offered.Should().OnlyContain(j => j.Availability == "Weekends only" || j.Availability == "Flexible");
    }

    [Fact]
    public async Task Offline_ranking_delivers_the_right_job_with_an_explainable_breakdown()
    {
        await using var db = await SeededDbAsync();
        var worker = await WorkerAsync(db, "La Nkwantanang Madina", "Full-time", "Block laying", "Plastering");

        var cards = await Service(db, new OfflineMatchRanker()).FindForWorkerAsync(worker, default);

        cards.Should().NotBeEmpty().And.HaveCountLessOrEqualTo(3);
        cards[0].Title.Should().Be("Block layer for 2-storey house");
        cards[0].Why.Should().Be("Needs your skill: Block laying; in your area");
        cards[0].Pay.Should().Be("GHS 180–250 per day");
        cards.Should().Contain(c => c.Title == "Plasterer and screeder" && c.Why.EndsWith("near your area"));

        var match = await db.Matches.SingleAsync(m => m.Id == cards[0].MatchId);
        match.Status.Should().Be(MatchStatus.Notified);
        match.Channel.Should().Be("conversation");
        match.Rank.Should().Be(1);
        var breakdown = JsonDocument.Parse(match.ScoreBreakdownJson!).RootElement;
        breakdown.GetProperty("ranker").GetString().Should().Be(OfflineMatchRanker.Name);
        breakdown.GetProperty("location").GetString().Should().Be("same district");
        breakdown.GetProperty("jobIsSynthetic").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Jobs_already_shown_in_conversation_are_not_shown_again()
    {
        await using var db = await SeededDbAsync();
        var worker = await WorkerAsync(db, "La Nkwantanang Madina", "Full-time", "Block laying");
        var first = await Service(db, new OfflineMatchRanker()).FindForWorkerAsync(worker, default);
        var second = await Service(db, new OfflineMatchRanker()).FindForWorkerAsync(worker, default);
        second.Select(c => c.Title).Should().NotIntersectWith(first.Select(c => c.Title));
    }

    [Fact]
    public async Task Workers_without_skills_or_area_get_nothing()
    {
        await using var db = await SeededDbAsync();
        var noSkills = await WorkerAsync(db, "La Nkwantanang Madina", "Full-time");
        (await Service(db, new OfflineMatchRanker()).FindForWorkerAsync(noSkills, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Alerts_go_once_to_nearby_confirmed_workers_with_a_utility_template()
    {
        await using var db = await SeededDbAsync();
        var near = await WorkerAsync(db, "La Nkwantanang Madina", "Full-time", "Tiling");   // borders Ayawaso West (East Legon)
        var far = await WorkerAsync(db, "Tema", "Full-time", "Tiling");
        var wa = new FakeWhatsAppClient();
        var alerts = new JobAlertService(db, new OfflineMatchRanker(), Service(db, new OfflineMatchRanker()), TestCatalog.Instance,
            wa, Options, NullLogger<JobAlertService>.Instance);
        var tiler = await db.JobPostings.SingleAsync(j => j.Title == "Tiler for bathrooms and kitchens");   // East Legon

        (await alerts.AlertForJobAsync(tiler.Id, default)).Should().Be(1);
        var sent = wa.Templates.Single();
        sent.WaId.Should().Be(near.WaId);
        sent.Name.Should().Be("job_match_alert");
        sent.Parameters.Should().Equal("Tiler for bathrooms and kitchens", "East Legon");
        sent.Payload.Should().Be("matches");
        (await db.Matches.SingleAsync()).Channel.Should().Be("alert");

        (await alerts.AlertForJobAsync(tiler.Id, default)).Should().Be(0, "never twice for the same job");
        var plasterer = await db.JobPostings.SingleAsync(j => j.Title == "Plasterer and screeder");
        db.WorkerSkills.Add(new WorkerSkill { WorkerId = near.Id, Skill = await db.Skills.SingleAsync(s => s.CanonicalName == "Plastering") });
        await db.SaveChangesAsync();
        (await alerts.AlertForJobAsync(plasterer.Id, default)).Should().Be(0, "at most one alert per worker per 24h");
        far.Id.Should().NotBe(near.Id);
    }

    [Fact]
    public async Task A_job_first_offered_by_alert_is_delivered_in_conversation_without_duplicating()
    {
        await using var db = await SeededDbAsync();
        var worker = await WorkerAsync(db, "La Nkwantanang Madina", "Full-time", "Tiling");
        var alerts = new JobAlertService(db, new OfflineMatchRanker(), Service(db, new OfflineMatchRanker()), TestCatalog.Instance,
            new FakeWhatsAppClient(), Options, NullLogger<JobAlertService>.Instance);
        var tiler = await db.JobPostings.SingleAsync(j => j.Title == "Tiler for bathrooms and kitchens");
        await alerts.AlertForJobAsync(tiler.Id, default);

        var cards = await Service(db, new OfflineMatchRanker()).FindForWorkerAsync(worker, default);

        cards.Should().Contain(c => c.Title == tiler.Title);
        var rows = await db.Matches.Where(m => m.JobPostingId == tiler.Id).ToListAsync();
        rows.Should().ContainSingle().Which.Channel.Should().Be("conversation");
    }

    [Fact]
    public void Http_ranker_parses_the_shared_match_contract()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "match_response.json"));
        var r = HttpMatchRanker.Parse(JsonDocument.Parse(json).RootElement);
        r.Ranker.Should().Be("sentence-transformers/paraphrase-MiniLM-L6-v2");
        r.Matches.Should().HaveCount(2);
        r.Matches[0].Should().BeEquivalentTo(new RankedMatch(1, 1, 0.688, "Block laying", 0.71, new[] { "Block laying" }));
    }

    [Fact]
    public void Template_payload_matches_the_Graph_API_shape()
    {
        var json = JsonDocument.Parse(JsonSerializer.Serialize(
            WhatsAppClient.BuildTemplatePayload("233200000001", "job_match_alert", "en", new[] { "Tiler", "East Legon" }, "matches"))).RootElement;
        json.GetProperty("type").GetString().Should().Be("template");
        var template = json.GetProperty("template");
        template.GetProperty("name").GetString().Should().Be("job_match_alert");
        var components = template.GetProperty("components");
        components[0].GetProperty("parameters")[1].GetProperty("text").GetString().Should().Be("East Legon");
        components[1].GetProperty("sub_type").GetString().Should().Be("quick_reply");
        components[1].GetProperty("parameters")[0].GetProperty("payload").GetString().Should().Be("matches");
    }

    [Theory]
    [InlineData("Full-time", "Part-time", true)]
    [InlineData("Part-time", "Full-time", false)]
    [InlineData("Weekends only", "Part-time", false)]
    [InlineData("Weekends only", "Flexible", true)]
    [InlineData("mornings only", "Full-time", true)]
    public void Availability_rules(string worker, string job, bool ok) => AvailabilityRules.Compatible(worker, job).Should().Be(ok);
}
