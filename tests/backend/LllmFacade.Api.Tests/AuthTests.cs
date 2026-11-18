using System.Net;
using System.Net.Http.Json;
using LllmFacade.Api.Services;
using Xunit;
using static LllmFacade.Api.Tests.Api;

namespace LllmFacade.Api.Tests;

public sealed class FirstLoginTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task FirstLogin_ForcesPasswordChangeBeforeAnyOtherApi()
    {
        var client = Anonymous(factory);

        var session = await (await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "admin" }, Token))
            .ReadAsync<SessionView>();
        Assert.True(session.MustChangePassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/endpoints", Token)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<SessionView>("/api/auth/me", Token))!.MustChangePassword);

        var weak = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "admin", newPassword = "corta" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("12", (await weak.JsonAsync()).GetProperty("error").GetString());

        var changed = await (await client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "admin", newPassword = AdminPassword }, Token)).ReadAsync<SessionView>();
        Assert.False(changed.MustChangePassword);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/endpoints", Token)).StatusCode);

        var other = Anonymous(factory);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await other.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "admin" }, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await other.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AdminPassword }, Token)).StatusCode);
    }
}

public sealed class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task AdminApis_RequireLogin()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(factory).GetAsync("/api/endpoints", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous(factory).GetAsync("/api/projects", Token)).StatusCode);
    }

    [Fact]
    public async Task UnsafeRequests_WithoutCsrfHeader_AreRejected()
    {
        var admin = await AdminAsync(factory);
        admin.DefaultRequestHeaders.Remove("X-Requested-With");

        var response = await admin.PostAsJsonAsync("/api/endpoints", EndpointRequest(), Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        await AdminAsync(factory);

        var response = await Anonymous(factory).PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "sbagliata" }, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        var admin = await AdminAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/auth/logout", null, Token)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/endpoints", Token)).StatusCode);
    }

    [Fact]
    public async Task PasswordChange_InvalidatesOtherSessions()
    {
        var first = await AdminAsync(factory);
        var second = await AdminAsync(factory);

        (await first.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = AdminPassword, newPassword = "Altra-Password-Lunga-1" }, Token)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/endpoints", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/endpoints", Token)).StatusCode);

        (await first.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Altra-Password-Lunga-1", newPassword = AdminPassword }, Token)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Health_IsPublicAndSendsSecurityHeaders()
    {
        var response = await Anonymous(factory).GetAsync("/health", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }
}

public sealed class LoginRateLimitTests(RateLimitedApiFactory factory) : IClassFixture<RateLimitedApiFactory>
{
    [Fact]
    public async Task Login_IsRateLimited()
    {
        var client = Anonymous(factory);
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 3; attempt++)
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "x" }, Token)).StatusCode);

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }
}
