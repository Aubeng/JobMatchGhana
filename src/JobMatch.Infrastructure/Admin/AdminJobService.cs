using JobMatch.Domain.Conversation;
using JobMatch.Domain.Entities;
using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Matching;
using Microsoft.EntityFrameworkCore;

namespace JobMatch.Infrastructure.Admin;

public record JobInput(
    string Title,
    string? Description,
    string TradeId,
    IReadOnlyList<string> Skills,
    string Area,
    decimal? PayMin,
    decimal? PayMax,
    string? PayPeriod,
    string? Availability,
    string? EmployerName);

public record JobSummary(
    int Id, string Title, string? Trade, string? Area, string? District, string Status, bool IsSynthetic,
    DateTimeOffset CreatedUtc, int Matches, int Interested, double? AverageRating);

public record JobDetail(
    int Id, string Title, string? Description, string? TradeId, IReadOnlyList<string> Skills, string? Area,
    string? District, decimal? PayMin, decimal? PayMax, string? PayPeriod, string? Availability, string? EmployerName,
    string Status, bool IsSynthetic, DateTimeOffset CreatedUtc);

/// <summary>Validation problems keyed by field, for the SPA form.</summary>
public class JobValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Invalid job")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>
/// Job listing CRUD for BOOST administrators (S&D L6; FR-17). Jobs start as drafts; publishing
/// opens them for matching and sends new-job alerts to suitable workers (DL-016).
/// Every job is validated against the same taxonomy and gazetteer the dialogue and NLU use, so
/// a published job is always matchable.
/// </summary>
public class AdminJobService
{
    public static readonly string[] PayPeriods = { "day", "week", "month", "job" };
    public static readonly string[] Availabilities = { "Full-time", "Part-time", "Weekends only", "Flexible" };

    private readonly JobMatchDbContext _db;
    private readonly DialogueCatalog _catalog;
    private readonly JobAlertService _alerts;

    public AdminJobService(JobMatchDbContext db, DialogueCatalog catalog, JobAlertService alerts)
    {
        _db = db;
        _catalog = catalog;
        _alerts = alerts;
    }

    public async Task<IReadOnlyList<JobSummary>> ListAsync(string? status, CancellationToken ct)
    {
        var query = _db.JobPostings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(j => j.Status == status);
        var jobs = await query.OrderByDescending(j => j.CreatedUtc).ThenByDescending(j => j.Id).ToListAsync(ct);
        var ids = jobs.Select(j => j.Id).ToList();
        var stats = await _db.Matches.AsNoTracking().Where(m => ids.Contains(m.JobPostingId))
            .GroupBy(m => m.JobPostingId)
            .Select(g => new
            {
                JobId = g.Key,
                Count = g.Count(),
                Interested = g.Count(m => m.InterestedUtc != null),
                Avg = g.Where(m => m.Rating != null).Average(m => (double?)m.Rating)
            })
            .ToDictionaryAsync(x => x.JobId, ct);
        return jobs.Select(j =>
        {
            stats.TryGetValue(j.Id, out var s);
            return new JobSummary(j.Id, j.Title, TradeName(j.TradeCategoryId), j.LocationText, j.District, j.Status,
                j.IsSynthetic, j.CreatedUtc, s?.Count ?? 0, s?.Interested ?? 0, s?.Avg is { } a ? Math.Round(a, 2) : null);
        }).ToList();
    }

    public async Task<JobDetail?> GetAsync(int id, CancellationToken ct)
    {
        var j = await _db.JobPostings.AsNoTracking().Include(x => x.Skills).ThenInclude(s => s.Skill)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return j is null ? null : Detail(j);
    }

    public async Task<JobDetail> CreateAsync(JobInput input, CancellationToken ct)
    {
        var job = new JobPosting { Status = "Draft", EmployerContact = "via JobMatch team" };
        await ApplyAsync(job, input, ct);
        _db.JobPostings.Add(job);
        await _db.SaveChangesAsync(ct);
        return Detail(job);
    }

    public async Task<JobDetail?> UpdateAsync(int id, JobInput input, CancellationToken ct)
    {
        var job = await _db.JobPostings.Include(x => x.Skills).ThenInclude(s => s.Skill).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null) return null;
        await ApplyAsync(job, input, ct);
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Detail(job);
    }

    /// <summary>Open the job for matching and alert suitable workers.</summary>
    public async Task<AlertResult?> PublishAsync(int id, CancellationToken ct)
    {
        var job = await _db.JobPostings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null) return null;
        job.Status = "Open";
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await _alerts.AlertForJobAsync(job.Id, ct);
    }

    public async Task<bool> CloseAsync(int id, CancellationToken ct)
    {
        var job = await _db.JobPostings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null) return false;
        job.Status = "Closed";
        job.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task ApplyAsync(JobPosting job, JobInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var trade = _catalog.TradeById(input.TradeId);
        var place = _catalog.FindLocation(input.Area ?? "");
        if (string.IsNullOrWhiteSpace(input.Title)) errors["title"] = new[] { "Enter a job title." };
        else if (input.Title.Length > 200) errors["title"] = new[] { "Keep the title under 200 characters." };
        if (input.Description?.Length > 2000) errors["description"] = new[] { "Keep the description under 2000 characters." };
        if (trade is null) errors["tradeId"] = new[] { "Choose a trade." };
        if (input.Skills is null || input.Skills.Count == 0) errors["skills"] = new[] { "Choose at least one skill." };
        else if (trade is not null && input.Skills.FirstOrDefault(s => trade.Skills.All(k => k.Name != s)) is { } bad)
            errors["skills"] = new[] { $"'{bad}' is not a skill in {trade.Name}." };
        if (place is null) errors["area"] = new[] { "Unknown area. Use a town, neighbourhood or district name." };
        if (input.PayMin < 0 || input.PayMax < 0) errors["payMin"] = new[] { "Pay can't be negative." };
        else if (input.PayMin is { } lo && input.PayMax is { } hi && lo > hi) errors["payMax"] = new[] { "The maximum must be at least the minimum." };
        if ((input.PayMin is not null || input.PayMax is not null) && !PayPeriods.Contains(input.PayPeriod))
            errors["payPeriod"] = new[] { "Choose per day, week, month or job." };
        if (input.Availability is not null && !Availabilities.Contains(input.Availability))
            errors["availability"] = new[] { "Choose full-time, part-time, weekends only or flexible." };
        if (errors.Count > 0) throw new JobValidationException(errors);

        job.Title = input.Title.Trim();
        job.Description = input.Description?.Trim();
        job.TradeCategoryId = trade!.Id;
        job.LocationText = place!.Label;
        job.District = place.District;
        job.Region = place.Region;
        job.PayMin = input.PayMin;
        job.PayMax = input.PayMax;
        job.PayPeriod = input.PayMin is null && input.PayMax is null ? null : input.PayPeriod;
        job.Availability = input.Availability;
        job.EmployerName = input.EmployerName?.Trim();

        job.Skills.Clear();
        foreach (var name in input.Skills!.Distinct())   // validated non-empty above
        {
            var skill = await _db.Skills.FirstOrDefaultAsync(s => s.CanonicalName == name, ct)
                        ?? _db.Skills.Add(new Skill { CanonicalName = name, Category = trade.Name }).Entity;
            job.Skills.Add(new JobPostingSkill { Skill = skill });
        }
    }

    private string? TradeName(string? id) => id is null ? null : _catalog.TradeById(id)?.Name;

    private static JobDetail Detail(JobPosting j) => new(
        j.Id, j.Title, j.Description, j.TradeCategoryId, j.Skills.Select(s => s.Skill!.CanonicalName).ToList(),
        j.LocationText, j.District, j.PayMin, j.PayMax, j.PayPeriod, j.Availability, j.EmployerName, j.Status,
        j.IsSynthetic, j.CreatedUtc);
}
