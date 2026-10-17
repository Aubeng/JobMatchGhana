using System.Security.Claims;
using System.Text;
using JobMatch.Domain.Conversation;
using JobMatch.Infrastructure.Admin;
using JobMatch.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;

namespace JobMatch.Api.Admin;

public record LoginRequest(string Email, string Password);
public record ResolveRequest(string? Note);

/// <summary>
/// The admin SPA's API (S&D L6): sign-in, job CRUD + publish, follow-ups, analytics, skills
/// supply, annotation, and pseudonymised exports. Everything except sign-in requires an
/// authenticated admin; every state change also requires <see cref="AdminAuth.CsrfHeader"/>.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").AddEndpointFilter(AdminAuth.RequireAdminHeader);
        auth.MapPost("/login", Login);
        auth.MapPost("/logout", async (SignInManager<AdminUser> signIn) => { await signIn.SignOutAsync(); return Results.NoContent(); });
        auth.MapGet("/me", (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
            ? Results.Ok(new { email = user.Identity.Name })
            : Results.Unauthorized());

        var admin = app.MapGroup("/api/admin").RequireAuthorization().AddEndpointFilter(AdminAuth.RequireAdminHeader);

        // Reference data for forms: the same taxonomy and gazetteer the bot and NLU use.
        admin.MapGet("/reference", (DialogueCatalog c) => Results.Ok(new
        {
            trades = c.Trades.Select(t => new { t.Id, t.Name, skills = t.Skills.Select(s => s.Name) }),
            areas = c.LocationMenu.Select(l => new { l.Label, l.District }),
            payPeriods = AdminJobService.PayPeriods,
            availabilities = AdminJobService.Availabilities,
            intents = ResearchService.Intents
        }));

        // Jobs
        admin.MapGet("/jobs", async (string? status, AdminJobService jobs, CancellationToken ct)
            => Results.Ok(await jobs.ListAsync(status, ct)));
        admin.MapGet("/jobs/{id:int}", async (int id, AdminJobService jobs, CancellationToken ct)
            => await jobs.GetAsync(id, ct) is { } job ? Results.Ok(job) : Results.NotFound());
        admin.MapPost("/jobs", async (JobInput input, AdminJobService jobs, CancellationToken ct)
            => await Validated(async () => Results.Ok(await jobs.CreateAsync(input, ct))));
        admin.MapPut("/jobs/{id:int}", async (int id, JobInput input, AdminJobService jobs, CancellationToken ct)
            => await Validated(async () => await jobs.UpdateAsync(id, input, ct) is { } job ? Results.Ok(job) : Results.NotFound()));
        admin.MapPost("/jobs/{id:int}/publish", async (int id, AdminJobService jobs, CancellationToken ct)
            => await jobs.PublishAsync(id, ct) is { } result ? Results.Ok(new { result.Alerted, result.Failed }) : Results.NotFound());
        admin.MapPost("/jobs/{id:int}/close", async (int id, AdminJobService jobs, CancellationToken ct)
            => await jobs.CloseAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Follow-ups (the only views with phone numbers: staff need them to connect people)
        admin.MapGet("/followups/interested", async (FollowUpService f, CancellationToken ct) => Results.Ok(await f.InterestedAsync(ct)));
        admin.MapPost("/followups/interested/{matchId:int}/connected", async (int matchId, FollowUpService f, CancellationToken ct)
            => await f.SetOutcomeAsync(matchId, true, ct) ? Results.NoContent() : Results.NotFound());
        admin.MapPost("/followups/interested/{matchId:int}/declined", async (int matchId, FollowUpService f, CancellationToken ct)
            => await f.SetOutcomeAsync(matchId, false, ct) ? Results.NoContent() : Results.NotFound());
        admin.MapGet("/followups/handoffs", async (FollowUpService f, CancellationToken ct) => Results.Ok(await f.OpenHandoffsAsync(ct)));
        admin.MapPost("/followups/handoffs/{id:int}/resolve", async (int id, ResolveRequest body, ClaimsPrincipal user,
                FollowUpService f, CancellationToken ct)
            => await f.ResolveHandoffAsync(id, user.Identity!.Name!, body.Note, ct) ? Results.NoContent() : Results.NotFound());

        // Research (anonymised)
        admin.MapGet("/analytics", async (ResearchService r, CancellationToken ct) => Results.Ok(await r.AnalyticsAsync(ct)));
        admin.MapGet("/supply", async (ResearchService r, CancellationToken ct) => Results.Ok(await r.SupplyAsync(ct)));
        admin.MapGet("/annotation/next", async (ResearchService r, CancellationToken ct)
            => await r.NextAnnotationAsync(ct) is { } task ? Results.Ok(task) : Results.NoContent());
        admin.MapGet("/annotation/summary", async (ResearchService r, CancellationToken ct) => Results.Ok(await r.AnnotationSummaryAsync(ct)));
        admin.MapPost("/annotation/{messageId:long}", async (long messageId, AnnotationInput input, ClaimsPrincipal user,
            ResearchService r, CancellationToken ct) =>
        {
            try
            {
                return await r.AnnotateAsync(messageId, input, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException e)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["intent"] = new[] { e.Message } });
            }
        });
        admin.MapGet("/export/messages.csv", (ResearchService r, CancellationToken ct) => Csv(() => r.MessagesCsvAsync(ct), "jobmatch-messages.csv"));
        admin.MapGet("/export/matches.csv", (ResearchService r, CancellationToken ct) => Csv(() => r.MatchesCsvAsync(ct), "jobmatch-matches.csv"));
        return app;
    }

    private static async Task<IResult> Login(LoginRequest body, SignInManager<AdminUser> signIn, UserManager<AdminUser> users)
    {
        var user = await users.FindByEmailAsync(body.Email ?? "");
        if (user is null)
            return Results.Problem("Wrong email or password.", statusCode: StatusCodes.Status401Unauthorized);
        var result = await signIn.PasswordSignInAsync(user, body.Password ?? "", isPersistent: false, lockoutOnFailure: true);
        if (result.IsLockedOut)
            return Results.Problem("Too many attempts. Try again in 15 minutes.", statusCode: StatusCodes.Status429TooManyRequests);
        return result.Succeeded
            ? Results.Ok(new { email = user.Email })
            : Results.Problem("Wrong email or password.", statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> Validated(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (JobValidationException e)
        {
            return Results.ValidationProblem(e.Errors.ToDictionary(kv => kv.Key, kv => kv.Value));
        }
    }

    private static async Task<IResult> Csv(Func<Task<string>> build, string fileName)
    {
        try
        {
            return Results.File(Encoding.UTF8.GetBytes(await build()), "text/csv", fileName);
        }
        catch (InvalidOperationException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
