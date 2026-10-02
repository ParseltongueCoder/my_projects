using UofSim.Core.Producers;
using UofSim.Host.Feed;

namespace UofSim.Host.Control;

public sealed record ReplayRequest(string File, double Speed = 1.0, bool Loop = false);

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
    }
}
