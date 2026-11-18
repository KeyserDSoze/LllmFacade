using LllmFacade.Api.Auth;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Business.ChatGpt;
using LllmFacade.Business.Endpoints;
using LllmFacade.Business.Security;
using LllmFacade.Business.Sessions;
using LllmFacade.Domain;
using LllmFacade.Infrastructure.Browser;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LllmFacade.Api.Endpoints;

public sealed record EndpointRequest(
    string? Name,
    List<ModelMapping>? Models,
    Dictionary<string, string>? ThinkingEfforts,
    bool? UseTemporaryChat,
    string? UserAgent,
    string? Locale,
    string? TimeZone,
    Dictionary<string, string>? ExtraHeaders,
    string? Cookies,
    string? AccessToken,
    int? RefreshIntervalMinutes,
    bool? IsEnabled);

public sealed record EndpointCreated(EndpointView Endpoint, string ApiKey);

public sealed record ApiKeyView(string ApiKey);

/// <summary>Admin configuration of the OpenAI-compatible endpoints; secrets are write-only.</summary>
public static class EndpointAdminApi
{
    public static void MapEndpointAdminApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/endpoints").RequireAuthorization(AdminAuth.AdminPolicy);
        group.MapGet("/", async (LllmFacadeDbContext db, CancellationToken cancellationToken) =>
            (await db.Endpoints.AsNoTracking().OrderBy(e => e.Name).ToListAsync(cancellationToken)).Select(EndpointView.From));
        group.MapGet("/{id:guid}", async (Guid id, LllmFacadeDbContext db, CancellationToken cancellationToken) =>
            await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken) is { } endpoint
                ? Results.Ok(EndpointView.From(endpoint))
                : NotFound());
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/api-key", RotateApiKeyAsync);
        group.MapPost("/{id:guid}/refresh", RefreshAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> CreateAsync(
        EndpointRequest request,
        LllmFacadeDbContext db,
        CredentialProtector protector,
        IOptions<ChatGptWebOptions> web,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var key = ApiKeys.Generate();
        var endpoint = new ChatGptEndpoint
        {
            Id = Guid.NewGuid(),
            ApiKeyHash = key.Hash,
            ApiKeyPrefix = key.LookupPrefix,
            CreatedAt = now,
            UpdatedAt = now
        };
        try
        {
            ApplySettings(endpoint, request);
            ApplyCredentials(endpoint, request, requireCookies: true, protector, web.Value.Host);
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new ApiError(error.Message));
        }

        db.Endpoints.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/endpoints/{endpoint.Id}", new EndpointCreated(EndpointView.From(endpoint), key.Key));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        EndpointRequest request,
        LllmFacadeDbContext db,
        CredentialProtector protector,
        IChatGptClient client,
        IOptions<ChatGptWebOptions> web,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await db.Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken) is not { } endpoint)
            return NotFound();
        var previousStamp = endpoint.CredentialsStamp;
        try
        {
            ApplySettings(endpoint, request);
            ApplyCredentials(endpoint, request, requireCookies: false, protector, web.Value.Host);
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new ApiError(error.Message));
        }

        endpoint.UpdatedAt = time.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ApiError("L'endpoint è stato modificato nel frattempo: ricaricare e riprovare."));
        }
        if (endpoint.CredentialsStamp != previousStamp || !endpoint.IsEnabled)
            await client.ReleaseAsync(endpoint.Id);
        return Results.Ok(EndpointView.From(endpoint));
    }

    private static async Task<IResult> RotateApiKeyAsync(
        Guid id, LllmFacadeDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var key = ApiKeys.Generate();
        var now = time.GetUtcNow();
        var updated = await db.Endpoints.Where(e => e.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(e => e.ApiKeyHash, key.Hash)
            .SetProperty(e => e.ApiKeyPrefix, key.LookupPrefix)
            .SetProperty(e => e.UpdatedAt, now), cancellationToken);
        return updated == 0 ? NotFound() : Results.Ok(new ApiKeyView(key.Key));
    }

    private static async Task<IResult> RefreshAsync(
        Guid id, LllmFacadeDbContext db, SessionRefreshProcessor refresher, CancellationToken cancellationToken)
    {
        if (await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken) is not { } endpoint)
            return NotFound();
        await refresher.RefreshAsync(endpoint, cancellationToken);
        return Results.Ok(EndpointView.From(await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == id, cancellationToken)));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, LllmFacadeDbContext db, IChatGptClient client, CancellationToken cancellationToken)
    {
        if (await db.Endpoints.Where(e => e.Id == id).ExecuteDeleteAsync(cancellationToken) == 0)
            return NotFound();
        await client.ReleaseAsync(id);
        return Results.NoContent();
    }

    private static void ApplySettings(ChatGptEndpoint endpoint, EndpointRequest request)
    {
        endpoint.Name = EndpointRules.NormalizeName(request.Name);
        endpoint.Models = [.. EndpointRules.NormalizeModels(request.Models)];
        endpoint.ThinkingEfforts = EndpointRules.NormalizeThinking(request.ThinkingEfforts);
        endpoint.UserAgent = EndpointRules.NormalizeUserAgent(request.UserAgent);
        endpoint.Locale = EndpointRules.NormalizeLocale(request.Locale);
        endpoint.TimeZone = EndpointRules.NormalizeTimeZone(request.TimeZone);
        endpoint.ExtraHeaders = EndpointRules.NormalizeHeaders(request.ExtraHeaders);
        endpoint.RefreshIntervalMinutes = EndpointRules.NormalizeRefreshInterval(request.RefreshIntervalMinutes);
        endpoint.UseTemporaryChat = request.UseTemporaryChat ?? true;
        endpoint.IsEnabled = request.IsEnabled ?? true;
    }

    private static void ApplyCredentials(
        ChatGptEndpoint endpoint, EndpointRequest request, bool requireCookies, CredentialProtector protector, string host)
    {
        var token = AccessTokens.Normalize(request.AccessToken);
        if (string.IsNullOrWhiteSpace(request.Cookies))
        {
            if (requireCookies)
                throw new ArgumentException("Inserire i cookie del browser.");
        }
        else
        {
            var cookies = CookieImport.Parse(request.Cookies, host);
            var storageState = StorageStates.FromCookies(cookies);
            endpoint.ProtectedStorageState = protector.Protect(storageState);
            endpoint.StorageStateHash = StorageStates.Hash(storageState);
            endpoint.CookieCount = cookies.Count;
            endpoint.CredentialsStamp = Guid.NewGuid();
            endpoint.LastRefreshAt = null;
            endpoint.LastRefreshAttemptAt = null;
            endpoint.LastError = null;
            endpoint.LastErrorAt = null;
        }

        if (token is not null)
        {
            endpoint.ProtectedAccessToken = protector.Protect(token);
            endpoint.AccessTokenExpiresAt = AccessTokens.ReadExpiry(token);
        }
    }

    private static IResult NotFound() => Results.NotFound(new ApiError("Endpoint non trovato."));
}
