using Microsoft.Extensions.Options;
using UofSim.Core.Recordings;
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

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SimOptions>(builder.Configuration.GetSection(SimOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProducerStateStore>();
builder.Services.AddSingleton<IFeedPublisher>(sp =>
    sp.GetRequiredService<IOptions<SimOptions>>().Value.AmqpEnabled
        ? ActivatorUtilities.CreateInstance<RabbitFeedPublisher>(sp)
        : ActivatorUtilities.CreateInstance<NullFeedPublisher>(sp));

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
app.MapControlApi();
app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();

public partial class Program;
