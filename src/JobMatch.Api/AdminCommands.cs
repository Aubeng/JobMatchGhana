using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Matching;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace JobMatch.Api;

/// <summary>
/// Operator commands run instead of the web host (until the admin SPA in Slice 4):
///   dotnet run --project src/JobMatch.Api -- seed-jobs [path]   load job postings (default: every Seed/jobs_synthetic_*.json)
///   dotnet run --project src/JobMatch.Api -- alert-job &lt;id&gt;      send new-job alerts for one posting
///   dotnet run --project src/JobMatch.Api -- create-admin &lt;email&gt; create an admin SPA account
///     (password typed hidden, or from the JOBMATCH_ADMIN_PASSWORD environment variable)
///   dotnet run --project src/JobMatch.Api -- migrate             apply pending database migrations
/// On the VPS these run through deploy/server/Invoke-AppCommand.ps1 (Deploy-Release.ps1 runs migrate).
/// </summary>
public static class AdminCommands
{
    public static async Task<bool> TryRunAsync(IServiceProvider services, string[] args)
    {
        if (args.Length == 0 || args[0] is not ("seed-jobs" or "alert-job" or "create-admin" or "migrate")) return false;

        using var scope = services.CreateScope();
        var ct = CancellationToken.None;
        switch (args[0])
        {
            case "seed-jobs":
                var paths = args.Length > 1
                    ? new[] { args[1] }
                    : Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Seed"), "jobs_synthetic_*.json").Order().ToArray();
                foreach (var path in paths)
                {
                    var (added, skipped) = await scope.ServiceProvider.GetRequiredService<JobSeeder>().SeedAsync(path, ct);
                    Console.WriteLine($"Seeded {added} job(s) from {Path.GetFileName(path)} ({skipped} already present).");
                }
                break;
            case "alert-job" when args.Length > 1 && int.TryParse(args[1], out var jobId):
                var result = await scope.ServiceProvider.GetRequiredService<JobAlertService>().AlertForJobAsync(jobId, ct);
                Console.WriteLine($"Job {jobId}: {result.Alerted} worker(s) alerted, {result.Failed} failed.");
                break;
            case "migrate":
                // The same migrations as migrate.sql, applied by EF Core itself: no sqlcmd needed.
                // Idempotent: already-applied migrations are skipped (__EFMigrationsHistory).
                var db = scope.ServiceProvider.GetRequiredService<JobMatchDbContext>();
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count == 0)
                {
                    Console.WriteLine("Database is up to date.");
                    break;
                }
                Console.WriteLine($"Applying {pending.Count} migration(s): {string.Join(", ", pending)}");
                await db.Database.MigrateAsync(ct);
                Console.WriteLine("Migrations applied.");
                break;
            case "create-admin" when args.Length > 1:
                await CreateAdminAsync(scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>(), args[1]);
                break;
            default:
                Console.Error.WriteLine("Usage: migrate | seed-jobs [path] | alert-job <jobId> | create-admin <email>");
                break;
        }
        return true;
    }

    private static async Task CreateAdminAsync(UserManager<AdminUser> users, string email)
    {
        var password = Environment.GetEnvironmentVariable("JOBMATCH_ADMIN_PASSWORD") ?? ReadHidden("Password (12+ characters): ");
        var user = new AdminUser { UserName = email, Email = email, EmailConfirmed = true, LockoutEnabled = true };
        var result = await users.CreateAsync(user, password);
        Console.WriteLine(result.Succeeded
            ? $"Admin {email} created."
            : "Failed: " + string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
            else chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }
}
