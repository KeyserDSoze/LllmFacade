using System.Text.Json.Serialization;
using LllmFacade.Api.Auth;
using LllmFacade.Api.Endpoints;
using LllmFacade.Api.OpenAi;
using LllmFacade.Api.Projects;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Domain;
using LllmFacade.Infrastructure.Browser;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LllmFacade.Api.Hosting;

public static class Startup
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public static WebApplicationBuilder AddLllmFacade(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var services = builder.Services;
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Configurare ConnectionStrings__Postgres (vedi docs/backend-chatgpt.md).");

        services.AddLllmFacadePostgres(connectionString);
        var dataProtection = services.AddDataProtection().SetApplicationName("LllmFacade");
        if (configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CredentialProtector>();
        services.AddSingleton<ProjectEventHub>();
        services.AddSingleton<AutopilotSignal>();
        services.AddSingleton<ActivityLog>();
        services.Configure<LogOptions>(configuration.GetSection("Logs"));
        services.AddScoped<EndpointIdentityService>();
        services.AddScoped<SessionRefreshProcessor>();
        services.AddScoped<AutopilotProcessor>();
        services.AddScoped<ProjectCommands>();
        services.Configure<WorkerOptions>(configuration.GetSection("Workers"));
        services.Configure<AutopilotOptions>(configuration.GetSection("Autopilot"));
        services.AddChatGptBrowser(configuration);
        services.AddAdminAuth(configuration);
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        if (configuration.GetValue("Workers:Enabled", true))
        {
            services.AddHostedService<SessionRefreshWorker>();
            services.AddHostedService<AutopilotWorker>();
            services.AddHostedService<LogRetentionWorker>();
        }
        return builder;
    }

    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<LllmFacadeDbContext>();
        await db.Database.MigrateAsync();
        await AdminAuth.SeedInitialAdminAsync(
            db, services.GetRequiredService<IPasswordHasher<AdminUser>>(), app.Configuration, services.GetRequiredService<TimeProvider>());
    }

    public static WebApplication UseLllmFacade(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            if (!context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/v1"))
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
            return next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(AdminAuth.CsrfGuardAsync);

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapAdminAuthApi();
        app.MapEndpointAdminApi();
        app.MapProjectAdminApi();
        app.MapActivityLogApi();
        app.MapOpenAiApi();
        return app;
    }
}
