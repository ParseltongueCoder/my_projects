using System.Text.Json.Serialization;
using Bo.Api.Identity;
using Bo.Api.Infrastructure;
using Bo.Api.Modules.Admin;
using Bo.Api.Modules.Catalog;
using Bo.Api.Modules.Config;
using Bo.Api.Modules.I18n;
using Bo.Api.Modules.Media;
using Bo.Api.Modules.Odds;
using Bo.Api.Modules.Platform;
using Npgsql;

// Operator back office API: tenants, admin users & RBAC, audit, configuration (BO-0); catalogue, translations, media (BO-1a);
// odds, trading, manual markets, messages (BO-1b).
// docs/06, docs/08, docs/09.
var builder = WebApplication.CreateBuilder(args);

var auth = builder.Configuration.GetSection(BoAuthOptions.Section).Get<BoAuthOptions>() ?? new BoAuthOptions();
var keycloak = builder.Configuration.GetSection(KeycloakAdminOptions.Section).Get<KeycloakAdminOptions>() ?? new KeycloakAdminOptions();

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Platform")
    ?? throw new InvalidOperationException("ConnectionStrings:Platform is not configured")));
builder.Services.AddSingleton<BoDb>();
builder.Services.AddSingleton<SettingsSnapshotCache>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<Names>();
builder.Services.AddSingleton<OfferService>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService<TradingExpiryWorker>();
if (string.IsNullOrWhiteSpace(keycloak.AdminUrl))
{
    builder.Services.AddSingleton<IIdentityProvisioner, NullIdentityProvisioner>();
}
else
{
    builder.Services.AddSingleton(keycloak);
    builder.Services.AddHttpClient<IIdentityProvisioner, KeycloakIdentityProvisioner>();
}
builder.Services.AddBoAuth(auth);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

var db = app.Services.GetRequiredService<NpgsqlDataSource>();
await CatalogSync.RunAsync(db);
if (app.Configuration.GetValue<bool>("Bo:DevSeed"))
{
    await DevSeed.RunAsync(db);
}

app.UseExceptionHandler();
app.UseCors();
app.UseMiddleware<BoProblemMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseWhen(c => c.Request.Path.StartsWithSegments("/api/bo"), b => b.UseMiddleware<TenantMiddleware>());

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();
app.MapPublicMedia();
var api = app.MapGroup("/api/bo");
api.MapAdmin();
api.MapConfig();
api.MapPlatform();
api.MapCatalog();
api.MapI18n();
api.MapMedia();
api.MapOdds();

app.Run();

public partial class Program;
