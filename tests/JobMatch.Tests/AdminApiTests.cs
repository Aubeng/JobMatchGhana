using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JobMatch.Api.Admin;
using JobMatch.Domain.Entities;
using JobMatch.Domain.Enums;
using JobMatch.Infrastructure.Admin;
using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Matching;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobMatch.Tests;

public class AdminApiFactory : WebhookPipelineFactory
{
    public const string Email = "admin@boost.test";
    public const string Password = "correct horse battery";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Research:PseudonymKey"] = "test-pseudonym-key-0123456789"
        }));
    }

    public async Task SeedAsync(Func<JobMatchDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        await seed(scope.ServiceProvider.GetRequiredService<JobMatchDbContext>());
    }

    public async Task<HttpClient> AdminClientAsync()
    {
        using (var scope = Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            if (await users.FindByEmailAsync(Email) is null)
                (await users.CreateAsync(new AdminUser { UserName = Email, Email = Email, LockoutEnabled = true }, Password))
                    .Succeeded.Should().BeTrue();
        }
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add(AdminAuth.CsrfHeader, "1");
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password })).EnsureSuccessStatusCode();
        return client;
    }
}

/// <summary>The admin SPA's API (S&D L6) over HTTP: auth, CSRF, jobs, follow-ups, research.</summary>
public class AdminApiTests : IClassFixture<AdminApiFactory>
{
    private readonly AdminApiFactory _factory;

    public AdminApiTests(AdminApiFactory factory) => _factory = factory;

    private static object ValidJob(string title = "Tiler for a new estate") => new
    {
        title, description = "Floor and wall tiles in 10 houses.", tradeId = "construction",
        skills = new[] { "Tiling" }, area = "Madina", payMin = 200m, payMax = 280m, payPeriod = "day",
        availability = "Full-time", employerName = "Estate developer"
    };

    [Fact]
    public async Task Admin_endpoints_require_sign_in()
    {
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/admin/jobs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_sign_in_sets_a_strict_cookie()
    {
        await _factory.AdminClientAsync();   // ensures the account exists
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add(AdminAuth.CsrfHeader, "1");

        var bad = await client.PostAsJsonAsync("/api/auth/login", new { email = AdminApiFactory.Email, password = "nope" });
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var ok = await client.PostAsJsonAsync("/api/auth/login", new { email = AdminApiFactory.Email, password = AdminApiFactory.Password });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = ok.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("jobmatch.admin="));
        cookie.Should().Contain("httponly").And.Contain("samesite=strict");
    }

    [Fact]
    public async Task State_changes_without_the_csrf_header_are_refused()
    {
        var client = await _factory.AdminClientAsync();
        client.DefaultRequestHeaders.Remove(AdminAuth.CsrfHeader);
        (await client.PostAsJsonAsync("/api/admin/jobs", ValidJob())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/admin/jobs")).StatusCode.Should().Be(HttpStatusCode.OK, "reads are fine");
    }

    [Fact]
    public async Task Invalid_jobs_return_field_errors()
    {
        var client = await _factory.AdminClientAsync();
        var r = await client.PostAsJsonAsync("/api/admin/jobs", new
        {
            title = "", tradeId = "construction", skills = new[] { "Braiding" }, area = "Atlantis", payMin = 300m, payMax = 100m, payPeriod = "day"
        });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        errors.EnumerateObject().Select(p => p.Name).Should().Contain(new[] { "title", "skills", "area", "payMax" });
    }

    [Fact]
    public async Task A_published_job_alerts_nearby_workers_and_appears_in_the_list()
    {
        var client = await _factory.AdminClientAsync();
        await _factory.SeedAsync(async db =>
        {
            var w = new Worker
            {
                WaId = "233200099001", ConsentGiven = true, IsActive = true, TradeName = "Construction & Building",
                LocationDistrict = "Adentan", Availability = "Full-time", ProfileConfirmedUtc = DateTimeOffset.UtcNow
            };
            db.Workers.Add(w);
            await db.SaveChangesAsync();
            db.WorkerSkills.Add(new WorkerSkill { WorkerId = w.Id, Skill = db.Skills.Local.FirstOrDefault(s => s.CanonicalName == "Tiling")
                ?? new Skill { CanonicalName = "Tiling" } });
            await db.SaveChangesAsync();
        });

        var created = await (await client.PostAsJsonAsync("/api/admin/jobs", ValidJob())).Content.ReadFromJsonAsync<JobDetail>();
        created!.Status.Should().Be("Draft");
        created.District.Should().Be("La Nkwantanang Madina");
        created.IsSynthetic.Should().BeFalse();

        var published = await client.PostAsync($"/api/admin/jobs/{created.Id}/publish", null);
        var body = await published.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("alerted").GetInt32().Should().Be(1);
        body.GetProperty("failed").GetInt32().Should().Be(0);
        _factory.WhatsApp.Templates.Should().Contain(t => t.WaId == "233200099001" && t.Name == "job_match_alert");

        var list = await client.GetFromJsonAsync<List<JobSummary>>("/api/admin/jobs?status=Open");
        list!.Should().Contain(j => j.Id == created.Id && j.Matches == 1);

        (await client.PostAsync($"/api/admin/jobs/{created.Id}/close", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JobDetail>($"/api/admin/jobs/{created.Id}"))!.Status.Should().Be("Closed");
    }

    [Fact]
    public async Task Interested_workers_and_handoffs_are_queued_for_staff()
    {
        var client = await _factory.AdminClientAsync();
        int matchId = 0, caseId = 0;
        await _factory.SeedAsync(async db =>
        {
            var w = new Worker { WaId = "233200099002", ConsentGiven = true, TradeName = "Hair & Beauty", LocationDistrict = "Ablekuma West" };
            var j = new JobPosting { Title = "Braider", Status = "Open", District = "Ablekuma West", LocationText = "Dansoman" };
            db.AddRange(w, j);
            await db.SaveChangesAsync();
            var m = new Match { WorkerId = w.Id, JobPostingId = j.Id, Status = MatchStatus.Interested, InterestedUtc = DateTimeOffset.UtcNow };
            var h = new HandoffCase { WaId = "233200099003", State = ConversationState.ElicitTrade };
            db.AddRange(m, h);
            db.MessageLogs.Add(new MessageLog { WaId = "233200099003", Direction = MessageDirection.Inbound, Body = "asdf", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
            (matchId, caseId) = (m.Id, h.Id);
        });

        var interested = await client.GetFromJsonAsync<List<InterestedItem>>("/api/admin/followups/interested");
        interested!.Should().Contain(i => i.MatchId == matchId && i.WorkerPhone == "233200099002" && i.JobTitle == "Braider");
        (await client.PostAsync($"/api/admin/followups/interested/{matchId}/connected", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<List<InterestedItem>>("/api/admin/followups/interested"))!.Should().NotContain(i => i.MatchId == matchId);

        var handoffs = await client.GetFromJsonAsync<List<HandoffItem>>("/api/admin/followups/handoffs");
        handoffs!.Should().Contain(h => h.Id == caseId && h.RecentMessages.Contains("asdf"));
        (await client.PostAsJsonAsync($"/api/admin/followups/handoffs/{caseId}/resolve", new { note = "Called; profile done." }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<List<HandoffItem>>("/api/admin/followups/handoffs"))!.Should().NotContain(h => h.Id == caseId);
    }

    [Fact]
    public async Task Annotation_is_blind_and_feeds_the_accuracy_metrics()
    {
        var client = await _factory.AdminClientAsync();
        const string entities = """[{"label":"SKILL_ITEM","text":"plastring","resolved":{"type":"skill","name":"Plastering"}},{"label":"SKILL_ITEM","text":"tiles","resolved":{"type":"skill","name":"Tiling"}}]""";
        await _factory.SeedAsync(async db =>
        {
            db.MessageLogs.AddRange(
                new MessageLog { WaId = "233200099010", Direction = MessageDirection.Inbound, Body = "i do plastring and tiles",
                    NluIntent = "SKILL_DECLARE", NluModelVersion = "intent@x;ner@y", NluEntitiesJson = entities, Timestamp = DateTimeOffset.UtcNow },
                new MessageLog { WaId = "233200099010", Direction = MessageDirection.Inbound, Body = "stub-analysed",
                    NluIntent = "unknown", NluModelVersion = "stub-fallback", Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        // Code every eligible message; only model-analysed ones are offered.
        var bodies = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            var r = await client.GetAsync("/api/admin/annotation/next");
            if (r.StatusCode == HttpStatusCode.NoContent) break;
            var raw = await r.Content.ReadAsStringAsync();
            raw.Should().NotContain("nluIntent").And.NotContain("SKILL_DECLARE", "coding is blind to the model's prediction");
            var task = JsonDocument.Parse(raw).RootElement;
            bodies.Add(task.GetProperty("text").GetString()!);
            var isOurs = task.GetProperty("text").GetString() == "i do plastring and tiles";
            if (isOurs) task.GetProperty("extractedSkills").GetArrayLength().Should().Be(2);
            (await client.PostAsJsonAsync($"/api/admin/annotation/{task.GetProperty("messageId").GetInt64()}",
                    new { intent = isOurs ? "SKILL_DECLARE" : "OTHER", skillsCorrect = isOurs ? 1 : (int?)null }))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        bodies.Should().Contain("i do plastring and tiles").And.NotContain("stub-analysed");

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/admin/annotation/summary");
        summary.GetProperty("intentAccuracy").GetProperty("value").GetDouble().Should().Be(1.0);
        summary.GetProperty("entityExtractionPrecision").GetProperty("value").GetDouble().Should().Be(0.5);

        (await client.PostAsJsonAsync("/api/admin/annotation/1", new { intent = "NOT_AN_INTENT" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Supply_suppresses_small_counts_and_analytics_report_targets()
    {
        var client = await _factory.AdminClientAsync();
        await _factory.SeedAsync(async db =>
        {
            for (var i = 0; i < 4; i++)
                db.Workers.Add(new Worker
                {
                    WaId = $"23320009910{i}", ConsentGiven = true, IsActive = true, ProfileConfirmedUtc = DateTimeOffset.UtcNow,
                    TradeName = i < 3 ? "Food & Catering" : "Agriculture & Agro-processing", LocationDistrict = "Ashaiman"
                });
            await db.SaveChangesAsync();
        });

        var supply = await client.GetFromJsonAsync<Supply>("/api/admin/supply");
        supply!.ByTradeAndDistrict.Should().Contain(c => c.Trade == "Food & Catering" && c.District == "Ashaiman" && c.Workers >= 3);
        supply.ByTradeAndDistrict.Should().Contain(c => c.Trade == "Agriculture & Agro-processing" && c.District == "Ashaiman" && c.Workers == null);

        var analytics = await client.GetFromJsonAsync<JsonElement>("/api/admin/analytics");
        analytics.GetProperty("taskCompletionRate").GetProperty("target").GetDouble().Should().Be(0.70);
        analytics.GetProperty("conversationEfficiency").GetProperty("higherIsBetter").GetBoolean().Should().BeFalse();
        analytics.GetProperty("matchingRelevance").GetProperty("target").GetDouble().Should().Be(0.60);
        analytics.GetProperty("funnel").GetProperty("consented").GetInt32().Should().BeGreaterThanOrEqualTo(4);
        analytics.GetRawText().Should().NotContain("23320009910", "analytics never carry phone numbers");
    }

    [Fact]
    public async Task Exports_are_pseudonymised()
    {
        var client = await _factory.AdminClientAsync();
        await _factory.SeedAsync(async db =>
        {
            db.MessageLogs.Add(new MessageLog { WaId = "233200099777", Direction = MessageDirection.Inbound, Body = "my name is Kofi",
                Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        var csv = await client.GetStringAsync("/api/admin/export/messages.csv");
        csv.Should().StartWith("pid,timestamp_utc");
        csv.Should().NotContain("233200099777").And.NotContain("Kofi");
        var pid = ResearchService.Pid(System.Text.Encoding.UTF8.GetBytes("test-pseudonym-key-0123456789"), "233200099777");
        csv.Should().Contain(pid);
        (await client.GetStringAsync("/api/admin/export/matches.csv")).Should().StartWith("pid,job_id");
    }

    [Fact]
    public async Task Exports_refuse_to_run_without_a_pseudonym_key()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var research = new ResearchService(scope.ServiceProvider.GetRequiredService<JobMatchDbContext>(),
            Options.Create(new ResearchOptions()));
        await FluentActions.Invoking(() => research.MessagesCsvAsync(default)).Should().ThrowAsync<InvalidOperationException>();
    }
}
