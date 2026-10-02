using Microsoft.Extensions.Options;
using UofSim.Core.Catalog;
using UofSim.Core.Recordings;
using UofSim.Core.Scenarios;
using UofSim.Core.SportsApi;
using UofSim.Host;
using UofSim.Host.Amqp;
using UofSim.Host.Control;
using UofSim.Host.Feed;
using UofSim.Host.Rest;

// `dotnet run -- generate-demo <path>` writes the synthetic demo recording and exits.
if (args is ["generate-demo", var output, ..])
{
    Recording.Write(output, DemoMatch.Build());
    Console.WriteLine($"Demo recording written to {output}");
    return;
}

// `dotnet run -- compile-scenario <scenario.yaml> <out.jsonl> [sport_id]` turns a scenario into a recording.
if (args is ["compile-scenario", var scenarioPath, var recordingPath, .. var rest])
{
    var sportId = rest is [var sport, ..] ? int.Parse(sport, System.Globalization.CultureInfo.InvariantCulture) : 1;
    Recording.Write(recordingPath, ScenarioCompiler.Compile(Scenario.Load(scenarioPath), sportId));
    Console.WriteLine($"Scenario compiled to {recordingPath}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SimOptions>(builder.Configuration.GetSection(SimOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProducerStateStore>();
builder.Services.AddSingleton<IFeedTransport>(sp =>
    sp.GetRequiredService<IOptions<SimOptions>>().Value.AmqpEnabled
        ? ActivatorUtilities.CreateInstance<RabbitFeedTransport>(sp)
        : ActivatorUtilities.CreateInstance<NullFeedTransport>(sp));
builder.Services.AddSingleton<IFeedPublisher, FeedPipeline>();
builder.Services.AddSingleton<FeedRecorder>();
builder.Services.AddSingleton<EventStateStore>();
builder.Services.AddSingleton(sp =>
    SimCatalog.Load(Path.Combine(sp.GetRequiredService<IOptions<SimOptions>>().Value.DataDir, "catalog.yaml")));
builder.Services.AddSingleton(sp => new SportsApiXml(
    sp.GetRequiredService<SimCatalog>(),
    sp.GetRequiredService<EventStateStore>(),
    sp.GetRequiredService<TimeProvider>().GetUtcNow()));

builder.Services.AddSingleton<RecoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecoveryService>());
builder.Services.AddSingleton<ReplayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReplayService>());
builder.Services.AddHostedService<AliveService>();

var app = builder.Build();

// Every 404 on the Betradar API means the SDK called an endpoint the simulator does not cover yet.
app.Use(async (ctx, next) =>
{
    await next();
    if (ctx.Response.StatusCode == StatusCodes.Status404NotFound && ctx.Request.Path.StartsWithSegments("/v1"))
    {
        app.Logger.LogWarning("Unsimulated endpoint: {Method} {Path}{Query}", ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString);
    }
});

app.MapBetradarApi();
app.MapSportsApi();
app.MapControlApi();
app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();

public partial class Program;
