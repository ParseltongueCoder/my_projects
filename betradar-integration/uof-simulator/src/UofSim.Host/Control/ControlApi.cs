using Microsoft.Extensions.Options;
using UofSim.Core.Catalog;
using UofSim.Core.Producers;
using UofSim.Core.Scenarios;
using UofSim.Host.Feed;

namespace UofSim.Host.Control;

public sealed record ReplayRequest(string File, double Speed = 1.0, bool Loop = false);

public sealed record PlayRequest(double Speed = 1.0, bool Loop = false);

public sealed record RecordRequest(string File);

/// <summary>Runtime control of the simulator (no token): producer chaos and recording replay.</summary>
public static class ControlApi
{
    public static void MapControlApi(this WebApplication app)
    {
        var sim = app.MapGroup("/sim");

        sim.MapGet("/status", (ProducerStateStore producers, ReplayService replay) => Results.Ok(new
        {
            producers = producers.All.Select(p => new { id = p.ProducerId, mode = p.Mode.ToString(), until = p.Until }),
            replay = replay.Status,
        }));

        // mode=silent: stop alive (consumer detects producer down); mode=unsubscribed: alive subscribed=0.
        sim.MapPost("/producers/{id:int}/down", (int id, string? mode, int? seconds, ProducerStateStore producers) =>
        {
            if (!producers.Exists(id))
            {
                return Results.NotFound(new { error = $"unknown producer {id}" });
            }
            var downMode = string.Equals(mode, "unsubscribed", StringComparison.OrdinalIgnoreCase)
                ? ProducerMode.Unsubscribed
                : ProducerMode.Silent;
            // A silent producer comes back unsubscribed, like a real producer after an outage.
            producers.Set(id, downMode, seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null, ProducerMode.Unsubscribed);
            return Results.Ok(producers.Get(id));
        });

        sim.MapPost("/producers/{id:int}/up", (int id, ProducerStateStore producers) =>
        {
            if (!producers.Exists(id))
            {
                return Results.NotFound(new { error = $"unknown producer {id}" });
            }
            producers.Set(id, ProducerMode.Up);
            return Results.Ok(producers.Get(id));
        });

        sim.MapPost("/replay", (ReplayRequest request, ReplayService replay) =>
        {
            try
            {
                return Results.Ok(replay.Start(request.File, request.Speed, request.Loop));
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        sim.MapPost("/replay/stop", (ReplayService replay) =>
        {
            replay.Stop();
            return Results.Ok(replay.Status);
        });

        sim.MapGet("/producers", () => Producers.All);

        sim.MapGet("/scenarios", (IOptions<SimOptions> o) =>
            Directory.EnumerateFiles(Path.Combine(o.Value.DataDir, "scenarios"), "*.yaml")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Order());

        sim.MapPost("/scenarios/{name}", (string name, PlayRequest? request, IOptions<SimOptions> o,
            SimCatalog catalog, ReplayService replay) =>
        {
            try
            {
                var (scenario, sportId) = LoadScenario(name, o.Value, catalog);
                var messages = ScenarioCompiler.Compile(scenario, sportId);
                return Results.Ok(replay.Play($"scenario:{name}", messages, request?.Speed ?? 1.0, request?.Loop ?? false));
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException
                                           or YamlDotNet.Core.YamlException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        sim.MapPost("/recorder/start", (RecordRequest request, FeedRecorder recorder) =>
        {
            try
            {
                return Results.Ok(recorder.Start(request.File));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        sim.MapPost("/recorder/stop", (FeedRecorder recorder) => Results.Ok(recorder.Stop()));
        sim.MapGet("/recorder", (FeedRecorder recorder) => Results.Ok(recorder.Status));
    }

    /// <summary>Loads <c>data/scenarios/{name}.yaml</c> and finds the sport of its event in the catalog.</summary>
    public static (Scenario Scenario, int SportId) LoadScenario(string name, SimOptions options, SimCatalog catalog)
    {
        var path = DataPaths.Resolve(options.DataDir, Path.Combine("scenarios", name + ".yaml"));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Scenario '{name}' not found");
        }
        var scenario = Scenario.Load(path);
        var ev = catalog.FindEvent(scenario.Event)
            ?? throw new InvalidDataException($"Event {scenario.Event} is not in data/catalog.yaml");
        return (scenario, catalog.FindTournament(ev.Tournament)!.Value.Sport.NumericId);
    }
}
