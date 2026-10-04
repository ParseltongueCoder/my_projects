using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Bo.Api.Infrastructure;

namespace Bo.Api.Identity;

public sealed record ProvisionedUser(Guid Id, string? TemporaryPassword);

/// <summary>
/// Keeps Keycloak (identity: login, password, 2FA, organization membership) in step with the back office
/// (permissions). docs/08 §3.2: one realm "bo", one Keycloak Organization per operator.
/// </summary>
public interface IIdentityProvisioner
{
    Task CreateOrganizationAsync(string alias, string name, CancellationToken ct);
    Task<ProvisionedUser> CreateUserAsync(string username, string email, string? displayName, string? organizationAlias, bool platformStaff, CancellationToken ct);
    Task SetEnabledAsync(Guid userId, bool enabled, CancellationToken ct);
}

/// <summary>No identity provider (tests, local runs without Keycloak): users get fresh ids, nothing else happens.</summary>
public sealed class NullIdentityProvisioner : IIdentityProvisioner
{
    public Task CreateOrganizationAsync(string alias, string name, CancellationToken ct) => Task.CompletedTask;

    public Task<ProvisionedUser> CreateUserAsync(string username, string email, string? displayName, string? organizationAlias, bool platformStaff, CancellationToken ct) =>
        Task.FromResult(new ProvisionedUser(Guid.NewGuid(), null));

    public Task SetEnabledAsync(Guid userId, bool enabled, CancellationToken ct) => Task.CompletedTask;
}

public sealed class KeycloakAdminOptions
{
    public const string Section = "Keycloak";

    /// <summary>Base URL the API reaches Keycloak on, e.g. <c>http://keycloak:8080</c>. Empty = no provisioning.</summary>
    public string AdminUrl { get; set; } = "";
    public string Realm { get; set; } = "bo";
    public string ClientId { get; set; } = "bo-api-admin";
    public string ClientSecret { get; set; } = "";

    /// <summary>Required actions for invited users: password change and TOTP enrolment (docs/08 §3.7).</summary>
    public string[] InviteRequiredActions { get; set; } = ["UPDATE_PASSWORD", "CONFIGURE_TOTP"];
}

/// <summary>Keycloak Admin REST API with a client-credentials service account (realm-management roles).</summary>
public sealed class KeycloakIdentityProvisioner(HttpClient http, KeycloakAdminOptions options) : IIdentityProvisioner
{
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string Admin(string path) => $"{options.AdminUrl.TrimEnd('/')}/admin/realms/{options.Realm}{path}";

    public async Task CreateOrganizationAsync(string alias, string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, Admin("/organizations"), new
        {
            name,
            alias,
            enabled = true,
            // Keycloak requires at least one domain; the internal placeholder is never verified or routed.
            domains = new[] { new { name = $"{alias}.operators.internal", verified = false } },
        }, ct);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            await EnsureSuccess(response, "create organization");
        }
    }

    public async Task<ProvisionedUser> CreateUserAsync(
        string username, string email, string? displayName, string? organizationAlias, bool platformStaff, CancellationToken ct)
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('/', 'x').Replace('+', 'y');
        using (var response = await SendAsync(HttpMethod.Post, Admin("/users"), new
        {
            username,
            email,
            firstName = displayName,
            enabled = true,
            emailVerified = true,
            requiredActions = options.InviteRequiredActions,
            credentials = new[] { new { type = "password", value = password, temporary = true } },
        }, ct))
        {
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw BoProblem.Conflict("USERNAME_TAKEN", $"User '{username}' already exists in the identity provider");
            }
            await EnsureSuccess(response, "create user");
        }

        var id = await FindUserIdAsync(username, ct);
        if (organizationAlias is not null)
        {
            var orgId = await FindOrganizationIdAsync(organizationAlias, ct);
            using var member = await SendAsync(HttpMethod.Post, Admin($"/organizations/{orgId}/members"), id.ToString(), ct);
            await EnsureSuccess(member, "add organization member");
        }
        if (platformStaff)
        {
            using var role = await SendAsync(HttpMethod.Get, Admin($"/roles/{BoClaims.PlatformStaffRole}"), null, ct);
            await EnsureSuccess(role, "read platform role");
            var representation = await role.Content.ReadFromJsonAsync<JsonElement>(ct);
            using var assign = await SendAsync(HttpMethod.Post, Admin($"/users/{id}/role-mappings/realm"), new[] { representation }, ct);
            await EnsureSuccess(assign, "assign platform role");
        }
        return new ProvisionedUser(id, password);
    }

    public async Task SetEnabledAsync(Guid userId, bool enabled, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, Admin($"/users/{userId}"), new { enabled }, ct);
        await EnsureSuccess(response, "update user");
        if (!enabled)
        {
            using var logout = await SendAsync(HttpMethod.Post, Admin($"/users/{userId}/logout"), null, ct);
            await EnsureSuccess(logout, "log out user");
        }
    }

    private async Task<Guid> FindUserIdAsync(string username, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Admin($"/users?exact=true&username={Uri.EscapeDataString(username)}"), null, ct);
        await EnsureSuccess(response, "find user");
        var users = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return users.GetArrayLength() == 1 ? users[0].GetProperty("id").GetGuid() : throw new InvalidOperationException($"User {username} not found after creation");
    }

    private async Task<string> FindOrganizationIdAsync(string alias, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Admin($"/organizations?search={Uri.EscapeDataString(alias)}&exact=true"), null, ct);
        await EnsureSuccess(response, "find organization");
        var orgs = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        foreach (var org in orgs.EnumerateArray())
        {
            if (org.TryGetProperty("alias", out var a) && a.GetString() == alias)
            {
                return org.GetProperty("id").GetString()!;
            }
        }
        throw BoProblem.Conflict("ORGANIZATION_MISSING", $"Keycloak organization '{alias}' does not exist");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await http.SendAsync(request, ct);
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires)
            {
                return _token;
            }
            using var response = await http.PostAsync(
                $"{options.AdminUrl.TrimEnd('/')}/realms/{options.Realm}/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = options.ClientId,
                    ["client_secret"] = options.ClientSecret,
                }), ct);
            await EnsureSuccess(response, "get admin token");
            var token = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            _token = token.GetProperty("access_token").GetString()!;
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32() - 30);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new BoProblem(StatusCodes.Status502BadGateway, "IDENTITY_PROVIDER_ERROR",
                $"Keycloak: {what} failed ({(int)response.StatusCode}) {body[..Math.Min(body.Length, 300)]}");
        }
    }
}
