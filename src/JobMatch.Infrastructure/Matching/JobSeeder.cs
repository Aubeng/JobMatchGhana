using System.Text.Json;
using JobMatch.Domain.Conversation;
using JobMatch.Domain.Entities;
using JobMatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace JobMatch.Infrastructure.Matching;

/// <summary>
/// Loads job postings from a seed file (e.g. <c>Seed/jobs_synthetic_v1.json</c>). Areas are
/// resolved to districts through the dialogue catalog's gazetteer and skills to taxonomy skills,
/// so seeded jobs are matchable exactly like real ones. Synthetic files mark every row
/// <see cref="JobPosting.IsSynthetic"/>. Idempotent: a job with the same title and district is
/// not added twice.
/// </summary>
public class JobSeeder
{
    private readonly JobMatchDbContext _db;
    private readonly DialogueCatalog _catalog;

    public JobSeeder(JobMatchDbContext db, DialogueCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<(int Added, int Skipped)> SeedAsync(string path, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(path, ct));
        var synthetic = doc.RootElement.GetProperty("version").GetString()?.Contains("synthetic", StringComparison.OrdinalIgnoreCase) ?? false;
        int added = 0, skipped = 0;
        foreach (var j in doc.RootElement.GetProperty("jobs").EnumerateArray())
        {
            var title = j.GetProperty("title").GetString()!;
            var area = j.GetProperty("area").GetString()!;
            var place = _catalog.FindLocation(area) ?? throw new InvalidDataException($"Unknown area '{area}' for job '{title}'.");
            if (await _db.JobPostings.AnyAsync(x => x.Title == title && x.District == place.District, ct))
            {
                skipped++;
                continue;
            }

            var tradeId = j.GetProperty("trade").GetString()!;
            var trade = _catalog.TradeById(tradeId) ?? throw new InvalidDataException($"Unknown trade '{tradeId}' for job '{title}'.");
            var job = new JobPosting
            {
                Title = title,
                Description = j.GetProperty("description").GetString(),
                LocationText = place.Label,
                District = place.District,
                Region = place.Region,
                TradeCategoryId = trade.Id,
                PayMin = j.GetProperty("pay_min").GetDecimal(),
                PayMax = j.GetProperty("pay_max").GetDecimal(),
                PayPeriod = j.GetProperty("pay_period").GetString(),
                Availability = j.GetProperty("availability").GetString(),
                EmployerName = j.GetProperty("employer").GetString(),
                EmployerContact = "via JobMatch team",
                IsSynthetic = synthetic,
                Status = "Open"
            };
            foreach (var name in j.GetProperty("skills").EnumerateArray().Select(s => s.GetString()!))
            {
                if (trade.Skills.All(s => s.Name != name))
                    throw new InvalidDataException($"Skill '{name}' is not in trade '{tradeId}' (job '{title}').");
                var skill = await _db.Skills.FirstOrDefaultAsync(s => s.CanonicalName == name, ct)
                            ?? _db.Skills.Local.FirstOrDefault(s => s.CanonicalName == name)
                            ?? _db.Skills.Add(new Skill { CanonicalName = name, Category = trade.Name }).Entity;
                job.Skills.Add(new JobPostingSkill { Skill = skill });
            }
            _db.JobPostings.Add(job);
            added++;
        }
        await _db.SaveChangesAsync(ct);
        return (added, skipped);
    }
}
