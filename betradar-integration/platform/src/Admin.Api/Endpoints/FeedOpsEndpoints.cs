using System.Security.Claims;
using Admin.Api.Data;
using Admin.Api.Infrastructure;

namespace Admin.Api.Endpoints;

public static class FeedOpsEndpoints
{
    public static void MapFeedOpsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(Policies.Viewer);

        api.MapGet("/me", (ClaimsPrincipal user) => new
        {
            name = user.Identity?.Name,
            roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Where(r => r.StartsWith("feedops-")).Distinct(),
        });

        api.MapGet("/overview", (AdminQueries q, CancellationToken ct) => q.OverviewAsync(ct));
        api.MapGet("/producers", (AdminQueries q, CancellationToken ct) => q.ProducersAsync(ct));

        api.MapGet("/events", (AdminQueries queries, string? status, string? q, DateTimeOffset? from, DateTimeOffset? to,
                int? page, int? pageSize, CancellationToken ct) =>
            queries.EventsAsync(status, q, from, to, page ?? 1, pageSize ?? 50, ct));

        api.MapGet("/events/{id:long}", async (long id, AdminQueries q, CancellationToken ct) =>
            await q.EventAsync(id, ct) is { } ev ? Results.Ok(ev) : Results.NotFound());
        api.MapGet("/events/{id:long}/settlements", (long id, AdminQueries q, CancellationToken ct) => q.SettlementsAsync(id, ct));
        api.MapGet("/events/{id:long}/bet-stops", (long id, AdminQueries q, CancellationToken ct) => q.BetStopsAsync(id, ct));

        api.MapGet("/messages", (AdminQueries q, string? type, string? status, string? eventUrn, DateTimeOffset? from,
                DateTimeOffset? to, int? page, int? pageSize, CancellationToken ct) =>
            q.MessagesAsync(type, status, eventUrn, from, to, page ?? 1, pageSize ?? 50, ct));
        api.MapGet("/messages/{id:long}", async (long id, AdminQueries q, CancellationToken ct) =>
            await q.MessageAsync(id, ct) is { } m ? Results.Ok(m) : Results.NotFound());

        // Server-sent events: one "change" event per canonical-model notification ({table, eventId, producerId}).
        api.MapGet("/stream", async (HttpContext http, ChangeFeed feed, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no"; // nginx: do not buffer the stream
            var (id, reader) = feed.Subscribe();
            try
            {
                await http.Response.WriteAsync(": connected\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
                using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
                var tick = heartbeat.WaitForNextTickAsync(ct).AsTask();
                while (!ct.IsCancellationRequested)
                {
                    var next = reader.WaitToReadAsync(ct).AsTask();
                    if (await Task.WhenAny(next, tick) == tick)
                    {
                        await http.Response.WriteAsync(": ping\n\n", ct);
                        tick = heartbeat.WaitForNextTickAsync(ct).AsTask();
                    }
                    else if (!await next)
                    {
                        break;
                    }
                    while (reader.TryRead(out var payload))
                    {
                        await http.Response.WriteAsync($"event: change\ndata: {payload}\n\n", ct);
                    }
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                feed.Unsubscribe(id);
            }
        });
    }
}
