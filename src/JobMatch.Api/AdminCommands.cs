using JobMatch.Infrastructure.Matching;

namespace JobMatch.Api;

/// <summary>
/// Operator commands run instead of the web host (until the admin SPA in Slice 4):
///   dotnet run --project src/JobMatch.Api -- seed-jobs [path]   load job postings (default: the synthetic set)
///   dotnet run --project src/JobMatch.Api -- alert-job &lt;id&gt;      send new-job alerts for one posting
/// Apply migrations first (dotnet ef database update).
/// </summary>
public static class AdminCommands
{
    public static async Task<bool> TryRunAsync(IServiceProvider services, string[] args)
    {
        if (args.Length == 0 || args[0] is not ("seed-jobs" or "alert-job")) return false;

        using var scope = services.CreateScope();
        var ct = CancellationToken.None;
        switch (args[0])
        {
            case "seed-jobs":
                var path = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "Seed", "jobs_synthetic_v1.json");
                var (added, skipped) = await scope.ServiceProvider.GetRequiredService<JobSeeder>().SeedAsync(path, ct);
                Console.WriteLine($"Seeded {added} job(s) from {path} ({skipped} already present).");
                break;
            case "alert-job" when args.Length > 1 && int.TryParse(args[1], out var jobId):
                var alerted = await scope.ServiceProvider.GetRequiredService<JobAlertService>().AlertForJobAsync(jobId, ct);
                Console.WriteLine($"Job {jobId}: {alerted} worker(s) alerted.");
                break;
            default:
                Console.Error.WriteLine("Usage: seed-jobs [path] | alert-job <jobId>");
                break;
        }
        return true;
    }
}
