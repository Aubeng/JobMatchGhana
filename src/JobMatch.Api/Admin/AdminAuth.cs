using JobMatch.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;

namespace JobMatch.Api.Admin;

/// <summary>
/// Admin sign-in for the SPA: ASP.NET Core Identity users with a session cookie. The SPA is
/// served by this API from /admin (same origin), so the cookie can be strict:
///   HttpOnly (no script access), SameSite=Strict (never sent cross-site), Secure over HTTPS,
///   8-hour sliding expiry; API calls get 401/403 rather than login redirects; lockout after
///   5 failed sign-ins; passwords of at least 12 characters.
/// </summary>
public static class AdminAuth
{
    /// <summary>Header every state-changing admin request must carry (see <see cref="RequireAdminHeader"/>).</summary>
    public const string CsrfHeader = "X-JobMatch-Admin";

    public static IServiceCollection AddAdminAuth(this IServiceCollection services, IHostEnvironment env)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddIdentityCore<AdminUser>(o =>
            {
                // Length over composition rules (NIST SP 800-63B): passphrases welcome.
                o.Password.RequiredLength = 12;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireDigit = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<JobMatchDbContext>()
            .AddSignInManager();

        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "jobmatch.admin";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// CSRF defence in depth on top of SameSite=Strict: a cross-site page can't add a custom
    /// header to a request without a CORS preflight, which this API never grants.
    /// </summary>
    public static async ValueTask<object?> RequireAdminHeader(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var request = ctx.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
            && !request.Headers.ContainsKey(CsrfHeader))
            return Results.Problem($"Missing {CsrfHeader} header.", statusCode: StatusCodes.Status400BadRequest);
        return await next(ctx);
    }
}
