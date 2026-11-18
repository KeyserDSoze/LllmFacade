using System.Security.Claims;
using System.Threading.RateLimiting;
using LllmFacade.Api.Services;
using LllmFacade.Business.Security;
using LllmFacade.Domain;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LllmFacade.Api.Auth;

public sealed record LoginRequest(string? UserName, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Admin login with a hashed password, mandatory first change, cookie session and CSRF header.</summary>
public static class AdminAuth
{
    public const string AdminPolicy = "Admin";
    public const string LoginRateLimit = "login";
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "LllmFacade";
    private const string MustChangePasswordClaim = "lf_must_change_password";
    private const string SecurityStampClaim = "lf_security_stamp";
    private static readonly AdminUser TimingDummy = new() { UserName = "dummy" };
    private static readonly Lazy<string> TimingDummyHash = new(() => new PasswordHasher<AdminUser>().HashPassword(TimingDummy, Guid.NewGuid().ToString()));

    public static IServiceCollection AddAdminAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "lllmfacade.admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = configuration.GetValue("Admin:RequireHttpsCookie", false)
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(MustChangePasswordClaim, "false"));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = configuration.GetValue("Admin:LoginAttemptsPerMinute", 10),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });
        return services;
    }

    /// <summary>Cookie-authenticated unsafe requests must carry a custom header, which cross-site forms cannot send.</summary>
    public static Task CsrfGuardAsync(HttpContext context, RequestDelegate next)
    {
        var method = context.Request.Method;
        var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
        if (unsafeMethod
            && context.Request.Path.StartsWithSegments("/api")
            && !string.Equals(context.Request.Headers[CsrfHeader], CsrfValue, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new ApiError("Richiesta rifiutata: header anti-CSRF mancante."));
        }
        return next(context);
    }

    public static void MapAdminAuthApi(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit);
        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization();
        auth.MapGet("/me", async (ClaimsPrincipal principal, LllmFacadeDbContext db, CancellationToken cancellationToken) =>
            await FindUserAsync(principal, db, cancellationToken) is { } user
                ? Results.Ok(new SessionView(user.UserName, user.MustChangePassword))
                : Results.Unauthorized()).RequireAuthorization();
        auth.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, LllmFacadeDbContext db, IPasswordHasher<AdminUser> hasher, HttpContext http, CancellationToken cancellationToken)
    {
        var userName = request.UserName?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        var user = userName.Length == 0 ? null : await db.AdminUsers.SingleOrDefaultAsync(u => u.UserName == userName, cancellationToken);
        if (user is null)
        {
            // Same hashing cost as an existing user: response time does not reveal valid user names.
            hasher.VerifyHashedPassword(TimingDummy, TimingDummyHash.Value, password);
            return InvalidCredentials();
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return InvalidCredentials();

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
            await db.SaveChangesAsync(cancellationToken);
        }
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(user));
        return Results.Ok(new SessionView(user.UserName, user.MustChangePassword));

        static IResult InvalidCredentials() =>
            Results.Json(new ApiError("Nome utente o password non validi."), statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        LllmFacadeDbContext db,
        IPasswordHasher<AdminUser> hasher,
        TimeProvider time,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (await FindUserAsync(principal, db, cancellationToken) is not { } user)
            return Results.Unauthorized();
        if (request.CurrentPassword is null
            || hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            return Results.BadRequest(new ApiError("La password attuale non è corretta."));

        var errors = PasswordRules.Validate(user.UserName, request.CurrentPassword, request.NewPassword);
        if (errors.Count > 0)
            return Results.BadRequest(new ApiError(errors[0], errors));

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword!);
        user.MustChangePassword = false;
        user.SecurityStamp = Guid.NewGuid();
        user.PasswordChangedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(user));
        return Results.Ok(new SessionView(user.UserName, user.MustChangePassword));
    }

    public static async Task SeedInitialAdminAsync(
        LllmFacadeDbContext db, IPasswordHasher<AdminUser> hasher, IConfiguration configuration, TimeProvider time)
    {
        if (await db.AdminUsers.AnyAsync())
            return;
        var user = new AdminUser
        {
            Id = Guid.NewGuid(),
            UserName = configuration["Admin:InitialUserName"] is { Length: > 0 } name ? name : "admin",
            MustChangePassword = true,
            SecurityStamp = Guid.NewGuid(),
            CreatedAt = time.GetUtcNow()
        };
        user.PasswordHash = hasher.HashPassword(user, configuration["Admin:InitialPassword"] is { Length: > 0 } password ? password : "admin");
        db.AdminUsers.Add(user);
        await db.SaveChangesAsync();
    }

    private static ClaimsPrincipal CreatePrincipal(AdminUser user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.UserName),
        new Claim(SecurityStampClaim, user.SecurityStamp.ToString()),
        new Claim(MustChangePasswordClaim, user.MustChangePassword ? "true" : "false")
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<AdminUser?> FindUserAsync(ClaimsPrincipal principal, LllmFacadeDbContext db, CancellationToken cancellationToken) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await db.AdminUsers.SingleOrDefaultAsync(u => u.Id == id, cancellationToken)
            : null;

    /// <summary>A password change rotates the security stamp and invalidates every other session.</summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var db = context.HttpContext.RequestServices.GetRequiredService<LllmFacadeDbContext>();
        var valid = principal is not null
            && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            && Guid.TryParse(principal.FindFirstValue(SecurityStampClaim), out var stamp)
            && await db.AdminUsers.AnyAsync(u => u.Id == id && u.SecurityStamp == stamp, context.HttpContext.RequestAborted);
        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
