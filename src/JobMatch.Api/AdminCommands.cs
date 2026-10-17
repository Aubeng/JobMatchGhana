using JobMatch.Infrastructure.Data;
using JobMatch.Infrastructure.Matching;
using Microsoft.AspNetCore.Identity;

namespace JobMatch.Api;

/// <summary>
/// Operator commands run instead of the web host (until the admin SPA in Slice 4):
///   dotnet run --project src/JobMatch.Api -- seed-jobs [path]   load job postings (default: the synthetic set)
///   dotnet run --project src/JobMatch.Api -- alert-job &lt;id&gt;      send new-job alerts for one posting
///   dotnet run --project src/JobMatch.Api -- create-admin &lt;email&gt; create an admin SPA account
///     (password typed hidden, or from the JOBMATCH_ADMIN_PASSWORD environment variable)
/// Apply migrations first (dotnet ef database update).
/// </summary>
public static class AdminCommands
{
    public static async Task<bool> TryRunAsync(IServiceProvider services, string[] args)
    {
        if (args.Length == 0 || args[0] is not ("seed-jobs" or "alert-job" or "create-admin")) return false;

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
                var result = await scope.ServiceProvider.GetRequiredService<JobAlertService>().AlertForJobAsync(jobId, ct);
                Console.WriteLine($"Job {jobId}: {result.Alerted} worker(s) alerted, {result.Failed} failed.");
                break;
            case "create-admin" when args.Length > 1:
                await CreateAdminAsync(scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>(), args[1]);
                break;
            default:
                Console.Error.WriteLine("Usage: seed-jobs [path] | alert-job <jobId> | create-admin <email>");
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
