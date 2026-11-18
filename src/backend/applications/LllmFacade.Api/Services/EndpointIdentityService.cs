using LllmFacade.Business.ChatGpt;
using LllmFacade.Business.Endpoints;
using LllmFacade.Domain;
using LllmFacade.Infrastructure.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LllmFacade.Api.Services;

/// <summary>Cookies and tokens are encrypted at rest with ASP.NET Core Data Protection.</summary>
public sealed class CredentialProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("LllmFacade.ChatGptCredentials.v1");

    public string Protect(string value) => _protector.Protect(value);

    public string Unprotect(string value) => _protector.Unprotect(value);
}

public sealed class EndpointIdentityService(
    LllmFacadeDbContext db,
    CredentialProtector protector,
    IChatGptClient client,
    ILogger<EndpointIdentityService> logger)
{
    public ChatGptIdentity CreateIdentity(ChatGptEndpoint endpoint) => new(
        endpoint.Id,
        endpoint.CredentialsStamp,
        protector.Unprotect(endpoint.ProtectedStorageState),
        endpoint.ProtectedAccessToken is null ? null : protector.Unprotect(endpoint.ProtectedAccessToken),
        endpoint.UserAgent,
        endpoint.Locale,
        endpoint.TimeZone,
        endpoint.ExtraHeaders);

    /// <summary>Stores the cookies the browser received from ChatGPT, unless the admin replaced the credentials meanwhile.</summary>
    public async Task PersistBrowserStateAsync(ChatGptEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            if (await client.CaptureStorageStateAsync(endpoint.Id, cancellationToken) is { } storageState)
                await SaveStorageStateAsync(endpoint.Id, endpoint.CredentialsStamp, storageState, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Cookie aggiornati non salvati per l'endpoint {EndpointId}.", endpoint.Id);
        }
    }

    public Task SaveStorageStateAsync(Guid endpointId, Guid credentialsStamp, string storageState, CancellationToken cancellationToken)
    {
        var hash = StorageStates.Hash(storageState);
        var count = StorageStates.CountCookies(storageState);
        var protectedState = protector.Protect(storageState);
        return db.Endpoints
            .Where(e => e.Id == endpointId && e.CredentialsStamp == credentialsStamp && e.StorageStateHash != hash)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.ProtectedStorageState, protectedState)
                .SetProperty(e => e.StorageStateHash, hash)
                .SetProperty(e => e.CookieCount, count), cancellationToken);
    }
}
