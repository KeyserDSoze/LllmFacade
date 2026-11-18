using System.Net;
using System.Net.Http.Json;
using LllmFacade.Api.Services;
using LllmFacade.Api.Workers;
using LllmFacade.Business.ChatGpt;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static LllmFacade.Api.Tests.Api;

namespace LllmFacade.Api.Tests;

public sealed class EndpointApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Create_ShowsApiKeyOnceAndNeverReturnsSecrets()
    {
        var admin = await AdminAsync(factory);

        var response = await admin.PostAsJsonAsync("/api/endpoints", EndpointRequest(), Token);
        var raw = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.JsonAsync();
        var apiKey = created.GetProperty("apiKey").GetString()!;
        Assert.StartsWith("lf_", apiKey);
        var endpoint = created.GetProperty("endpoint");
        Assert.Equal(apiKey[..11], endpoint.GetProperty("apiKeyPrefix").GetString());
        Assert.True(endpoint.GetProperty("hasAccessToken").GetBoolean());
        Assert.Equal(2, endpoint.GetProperty("cookieCount").GetInt32());
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), endpoint.GetProperty("accessTokenExpiresAt").GetDateTimeOffset());
        Assert.DoesNotContain("segreto-sessione", raw);

        var id = endpoint.GetProperty("id").GetGuid();
        var listed = await admin.GetStringAsync("/api/endpoints", Token);
        Assert.DoesNotContain("segreto-sessione", listed);
        Assert.DoesNotContain(apiKey, listed);
        Assert.DoesNotContain(apiKey, await admin.GetStringAsync($"/api/endpoints/{id}", Token));

        await factory.WithDbAsync(async db =>
        {
            var stored = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == id, Token);
            Assert.DoesNotContain("segreto-sessione", stored.ProtectedStorageState);
            Assert.DoesNotContain("firma", stored.ProtectedAccessToken!);
            Assert.DoesNotContain(apiKey, stored.ApiKeyHash);
        });
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("userAgent", "")]
    [InlineData("cookies", "")]
    [InlineData("cookies", "senza-uguale")]
    public async Task Create_RejectsInvalidData(string field, string value)
    {
        var admin = await AdminAsync(factory);
        var request = System.Text.Json.JsonSerializer.SerializeToNode(EndpointRequest(), Json)!.AsObject();
        request[field] = value;

        var response = await admin.PostAsJsonAsync("/api/endpoints", request, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await response.JsonAsync()).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Create_RejectsReservedHeaders()
    {
        var admin = await AdminAsync(factory);
        var request = System.Text.Json.JsonSerializer.SerializeToNode(EndpointRequest(), Json)!.AsObject();
        request["extraHeaders"] = new System.Text.Json.Nodes.JsonObject { ["Cookie"] = "a=1" };

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/endpoints", request, Token)).StatusCode);
    }

    [Fact]
    public async Task Update_ReplacesCookiesOnlyWhenProvided()
    {
        var admin = await AdminAsync(factory);
        var (id, _) = await CreateEndpointAsync(admin);
        var request = System.Text.Json.JsonSerializer.SerializeToNode(EndpointRequest(), Json)!.AsObject();
        request["cookies"] = null;
        request["accessToken"] = null;
        request["name"] = "Rinominato";

        var renamed = await (await admin.PutAsJsonAsync($"/api/endpoints/{id}", request, Token)).ReadAsync<EndpointView>();
        Assert.Equal("Rinominato", renamed.Name);
        Assert.Equal(2, renamed.CookieCount);
        Assert.DoesNotContain(id, factory.ChatGpt.Released);

        request["cookies"] = "uno=1; due=2; tre=3";
        var replaced = await (await admin.PutAsJsonAsync($"/api/endpoints/{id}", request, Token)).ReadAsync<EndpointView>();
        Assert.Equal(3, replaced.CookieCount);
        Assert.Contains(id, factory.ChatGpt.Released);
    }

    [Fact]
    public async Task RotateKey_InvalidatesThePreviousKey()
    {
        var admin = await AdminAsync(factory);
        var (id, oldKey) = await CreateEndpointAsync(admin);

        var newKey = (await (await admin.PostAsync($"/api/endpoints/{id}/api-key", null, Token)).JsonAsync()).GetProperty("apiKey").GetString();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", oldKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/models", Token)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", newKey);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models", Token)).StatusCode);
    }

    [Fact]
    public async Task Refresh_StoresNewTokenCookiesAndAccount()
    {
        var admin = await AdminAsync(factory);
        var (id, _) = await CreateEndpointAsync(admin);

        var refreshed = await (await admin.PostAsync($"/api/endpoints/{id}/refresh", null, Token)).ReadAsync<EndpointView>();

        Assert.Equal(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero), refreshed.AccessTokenExpiresAt);
        Assert.Equal("utente@example.com", refreshed.AccountEmail);
        Assert.Equal(3, refreshed.CookieCount);
        Assert.NotNull(refreshed.LastRefreshAt);
        Assert.Null(refreshed.LastError);
        var identity = factory.ChatGpt.Identities.Last(i => i.EndpointId == id);
        Assert.Contains("segreto-sessione", identity.StorageStateJson);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Test", identity.UserAgent);
        Assert.Equal("it-IT,it;q=0.9", identity.ExtraHeaders["Accept-Language"]);
    }

    [Fact]
    public async Task Refresh_FailureIsVisibleOnTheEndpoint()
    {
        var admin = await AdminAsync(factory);
        var (id, _) = await CreateEndpointAsync(admin);
        factory.ChatGpt.Failure = new ChatGptException(ChatGptFailure.SessionExpired, "Sessione scaduta: reinserire i cookie.");
        try
        {
            var endpoint = await (await admin.PostAsync($"/api/endpoints/{id}/refresh", null, Token)).ReadAsync<EndpointView>();

            Assert.Equal("Sessione scaduta: reinserire i cookie.", endpoint.LastError);
            Assert.NotNull(endpoint.LastErrorAt);
            Assert.Null(endpoint.LastRefreshAt);
        }
        finally
        {
            factory.ChatGpt.Failure = null;
        }
    }

    [Fact]
    public async Task BackgroundRefresh_RenewsDueEndpointsAndPersistsRotatedCookies()
    {
        var admin = await AdminAsync(factory);
        var (id, _) = await CreateEndpointAsync(admin);

        await factory.WithServiceAsync<SessionRefreshProcessor>(processor => processor.RefreshDueAsync(Token));
        var endpoint = await admin.GetFromJsonAsync<EndpointView>($"/api/endpoints/{id}", Json, Token);
        Assert.NotNull(endpoint!.LastRefreshAt);

        factory.ChatGpt.LiveStorageState = """{"cookies":[{"name":"a","value":"1","domain":".chatgpt.com","path":"/","expires":-1,"httpOnly":false,"secure":true,"sameSite":"Lax"}],"origins":[]}""";
        try
        {
            await factory.WithServiceAsync<EndpointIdentityService>(async identities =>
            {
                await factory.WithDbAsync(async db =>
                    await identities.PersistBrowserStateAsync(await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == id, Token), Token));
            });
            Assert.Equal(1, (await admin.GetFromJsonAsync<EndpointView>($"/api/endpoints/{id}", Json, Token))!.CookieCount);
        }
        finally
        {
            factory.ChatGpt.LiveStorageState = null;
        }
    }

    [Fact]
    public async Task Delete_RemovesEndpointAndClosesItsBrowser()
    {
        var admin = await AdminAsync(factory);
        var (id, _) = await CreateEndpointAsync(admin);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/endpoints/{id}", Token)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/endpoints/{id}", Token)).StatusCode);
        Assert.Contains(id, factory.ChatGpt.Released);
    }
}
