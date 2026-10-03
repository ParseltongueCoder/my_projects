using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Admin.Api.Infrastructure;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>false only for local development and tests: every request is a viewer + operator.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Issuer as the browser sees it, e.g. <c>http://localhost:8180/realms/feedops</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>Optional: where the API itself fetches OIDC metadata (inside Docker: <c>http://keycloak:8080/...</c>).</summary>
    public string? MetadataAddress { get; set; }

    public string Audience { get; set; } = "admin-api";
    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class Policies
{
    public const string Viewer = "feedops-viewer";
    public const string Operator = "feedops-operator";

    public static IServiceCollection AddFeedOpsAuth(this IServiceCollection services, AuthOptions options)
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
                jwt.TokenValidationParameters.NameClaimType = "preferred_username";
                jwt.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
            });
            services.AddTransient<IClaimsTransformation, KeycloakRoles>();
        }
        else
        {
            services.AddAuthentication(DevAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
        }

        services.AddAuthorizationBuilder()
            // An operator can do everything a viewer can.
            .AddPolicy(Viewer, p => p.RequireRole(Viewer, Operator))
            .AddPolicy(Operator, p => p.RequireRole(Operator))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
}

/// <summary>Keycloak puts realm roles in <c>realm_access.roles</c>; turn them into role claims.</summary>
internal sealed class KeycloakRoles : IClaimsTransformation
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

/// <summary>Auth disabled: a fixed developer identity with both roles.</summary>
internal sealed class DevAuthHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("preferred_username", "developer"),
            new Claim(ClaimTypes.Role, Policies.Viewer),
            new Claim(ClaimTypes.Role, Policies.Operator),
        ], SchemeName, "preferred_username", ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
