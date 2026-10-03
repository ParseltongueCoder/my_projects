using System.Text.Json.Serialization;
using Admin.Api.Data;
using Admin.Api.Endpoints;
using Admin.Api.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var auth = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
var simulator = builder.Configuration.GetSection(SimulatorOptions.Section).Get<SimulatorOptions>() ?? new SimulatorOptions();

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Platform")
    ?? throw new InvalidOperationException("ConnectionStrings:Platform is not configured")));
builder.Services.AddSingleton<AdminQueries>();
builder.Services.AddSingleton<ChangeFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChangeFeed>());
builder.Services.AddFeedOpsAuth(auth);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never);
if (!string.IsNullOrWhiteSpace(simulator.BaseUrl))
{
    builder.Services.AddHttpClient("simulator", c => c.BaseAddress = new Uri(simulator.BaseUrl));
}

// Only needed when the Angular dev server (ng serve) calls the API directly; behind nginx it is same-origin.
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();
app.MapFeedOpsApi();
app.MapSimulatorApi(simulator);

app.Run();

public partial class Program;
