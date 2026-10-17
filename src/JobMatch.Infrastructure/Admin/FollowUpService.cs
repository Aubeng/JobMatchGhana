using JobMatch.Domain.Enums;
using JobMatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace JobMatch.Infrastructure.Admin;

public record InterestedItem(int MatchId, string WorkerPhone, string? WorkerTrade, string? WorkerArea, int JobId,
    string JobTitle, string? JobArea, string? Employer, bool JobIsSynthetic, DateTimeOffset InterestedUtc);

public record HandoffItem(int Id, string WorkerPhone, string State, DateTimeOffset CreatedUtc,
    IReadOnlyList<string> RecentMessages);

/// <summary>
/// The operational queue for BOOST staff. These are the only admin views that show a worker's
/// phone number: staff need it to make the connection a worker asked for ("I'm interested") or
/// to help one the bot couldn't (3-strike handoff). Analytics views never show it (NFR-12).
/// </summary>
public class FollowUpService
{
    private readonly JobMatchDbContext _db;

    public FollowUpService(JobMatchDbContext db) => _db = db;

    public async Task<IReadOnlyList<InterestedItem>> InterestedAsync(CancellationToken ct)
        => await _db.Matches.AsNoTracking()
            .Where(m => m.Status == MatchStatus.Interested)
            .OrderBy(m => m.InterestedUtc)
            .Select(m => new InterestedItem(m.Id, m.Worker!.WaId, m.Worker.TradeName,
                m.Worker.LocationText ?? m.Worker.LocationDistrict, m.JobPostingId, m.JobPosting!.Title,
                m.JobPosting.LocationText, m.JobPosting.EmployerName, m.JobPosting.IsSynthetic, m.InterestedUtc!.Value))
            .ToListAsync(ct);

    /// <summary>Record the outcome of a connection: <c>Connected</c> or <c>Declined</c>.</summary>
    public async Task<bool> SetOutcomeAsync(int matchId, bool connected, CancellationToken ct)
    {
        var match = await _db.Matches.FirstOrDefaultAsync(m => m.Id == matchId, ct);
        if (match is null) return false;
        match.Status = connected ? MatchStatus.Connected : MatchStatus.Declined;
        match.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<HandoffItem>> OpenHandoffsAsync(CancellationToken ct)
    {
        var cases = await _db.HandoffCases.AsNoTracking().Where(h => h.ResolvedUtc == null)
            .OrderBy(h => h.CreatedUtc).ToListAsync(ct);
        var items = new List<HandoffItem>();
        foreach (var h in cases)
        {
            // The worker's last few messages, so staff see what went wrong before calling.
            var recent = await _db.MessageLogs.AsNoTracking()
                .Where(m => m.WaId == h.WaId && m.Direction == MessageDirection.Inbound && m.Timestamp <= h.CreatedUtc.AddMinutes(1))
                .OrderByDescending(m => m.Timestamp).Take(5).Select(m => m.Body ?? "").ToListAsync(ct);
            recent.Reverse();
            items.Add(new HandoffItem(h.Id, h.WaId, h.State.ToString(), h.CreatedUtc, recent));
        }
        return items;
    }

    public async Task<bool> ResolveHandoffAsync(int id, string resolvedBy, string? note, CancellationToken ct)
    {
        var h = await _db.HandoffCases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null) return false;
        h.ResolvedUtc = DateTimeOffset.UtcNow;
        h.ResolvedBy = resolvedBy;
        h.Note = note?.Length > 1000 ? note[..1000] : note;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
