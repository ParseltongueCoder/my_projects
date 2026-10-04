using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Bo.Api.Infrastructure;

public sealed class BoAuthOptions
{
    public const string Section = "Auth";

    /// <summary>false only for local development and tests: the identity comes from X-Dev-* headers.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Issuer as the browser sees it, e.g. <c>http://localhost:8180/realms/bo</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>Optional: where the API fetches OIDC metadata (inside Docker: <c>http://keycloak:8080/...</c>).</summary>
    public string? MetadataAddress { get; set; }

    public string Audience { get; set; } = "bo-api";
    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>Claims Bo.Api reads from a Keycloak realm "bo" token.</summary>
public static class BoClaims
{
    public const string Subject = "sub";
    public const string Username = "preferred_username";
    public const string Name = "name";

    /// <summary>Keycloak 26 Organizations mapper: <c>["acmebet"]</c> or <c>{"acmebet": {"id": "..."}}</c>.</summary>
    public const string Organization = "organization";

    /// <summary>Realm role marking our own staff (members of no organization).</summary>
    public const string PlatformStaffRole = "platform-staff";

    public static IReadOnlyList<string> Organizations(ClaimsPrincipal user)
    {
        var aliases = new List<string>();
        foreach (var claim in user.FindAll(Organization))
        {
            var value = claim.Value.Trim();
            if (value.StartsWith('{') || value.StartsWith('['))
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    aliases.AddRange(doc.RootElement.EnumerateObject().Select(p => p.Name));
                }
                else
                {
                    aliases.AddRange(doc.RootElement.EnumerateArray().Select(e => e.GetString()).OfType<string>());
                }
            }
            else if (value.Length > 0)
            {
                aliases.Add(value);
            }
        }
        return aliases;
    }

    public static bool IsPlatformStaff(ClaimsPrincipal user) => user.IsInRole(PlatformStaffRole);

    public static IServiceCollection AddBoAuth(this IServiceCollection services, BoAuthOptions options)
    {
        if (options.Enabled)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.MetadataAddress = options.MetadataAddress ?? "";
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.ValidIssuer = options.Authority;
                jwt.TokenValidationParameters.NameClaimType = Username;
                jwt.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                // Keycloak starts alongside us in compose; retry metadata quickly instead of the 5 min default.
                jwt.RefreshInterval = TimeSpan.FromSeconds(10);
            });
            services.AddTransient<IClaimsTransformation, KeycloakRealmRoles>();
        }
        else
        {
            services.AddAuthentication(DevHeaderAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevHeaderAuthHandler>(DevHeaderAuthHandler.SchemeName, _ => { });
        }
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
}

/// <summary>Keycloak puts realm roles in <c>realm_access.roles</c>; turn them into role claims.</summary>
internal sealed class KeycloakRealmRoles : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || identity.HasClaim(c => c.Type == ClaimTypes.Role)
            || principal.FindFirst("realm_access")?.Value is not { } json)
        {
            return Task.FromResult(principal);
        }
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
        {
            foreach (var role in roles.EnumerateArray())
            {
                if (role.GetString() is { } r)
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, r));
                }
            }
        }
        return Task.FromResult(principal);
    }
}

/// <summary>
/// Auth disabled (dev / tests): <c>X-Dev-User</c> (user id), <c>X-Dev-Org</c> (organization alias),
/// <c>X-Dev-Platform: 1</c> (platform staff). Without headers: the seeded platform super admin.
/// </summary>
internal sealed class DevHeaderAuthHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevHeaders";
    public const string DefaultUser = "10000000-0000-0000-0000-000000000001";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers;
        var user = headers["X-Dev-User"].FirstOrDefault() ?? DefaultUser;
        var claims = new List<Claim> { new(BoClaims.Subject, user), new(BoClaims.Username, headers["X-Dev-Name"].FirstOrDefault() ?? "developer") };
        if (headers["X-Dev-Org"].FirstOrDefault() is { Length: > 0 } org)
        {
            claims.Add(new Claim(BoClaims.Organization, org));
        }
        if (headers["X-Dev-User"].Count == 0 || headers["X-Dev-Platform"].FirstOrDefault() == "1")
        {
            claims.Add(new Claim(ClaimTypes.Role, BoClaims.PlatformStaffRole));
        }
        var identity = new ClaimsIdentity(claims, SchemeName, BoClaims.Username, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
