using Admin.Api.Infrastructure;

namespace Admin.Api.Endpoints;

public sealed class SimulatorOptions
{
    public const string Section = "Simulator";

    /// <summary>Base URL of the UofSim control API (e.g. <c>http://uofsim:8080</c>). Empty = panel disabled (production).</summary>
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// Dev/demo only: forwards a fixed set of simulator control calls so the Feed Ops UI can drive demos
/// (start a scenario, take a producer down). Not mapped at all when no simulator is configured.
/// </summary>
public static class SimulatorEndpoints
{
    public static void MapSimulatorApi(this WebApplication app, SimulatorOptions options)
    {
        var sim = app.MapGroup("/api/sim");
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            sim.MapGet("/status", () => Results.Ok(new { enabled = false })).RequireAuthorization(Policies.Viewer);
            return;
        }

        sim.MapGet("/status", (IHttpClientFactory http, CancellationToken ct) => Forward(http, HttpMethod.Get, "/sim/status", null, ct))
            .RequireAuthorization(Policies.Viewer);
        sim.MapGet("/scenarios", (IHttpClientFactory http, CancellationToken ct) => Forward(http, HttpMethod.Get, "/sim/scenarios", null, ct))
            .RequireAuthorization(Policies.Viewer);

        var ops = sim.MapGroup("").RequireAuthorization(Policies.Operator);
        ops.MapPost("/scenarios/{name}", (string name, PlayRequest? body, IHttpClientFactory http, CancellationToken ct) =>
            Forward(http, HttpMethod.Post, $"/sim/scenarios/{Uri.EscapeDataString(name)}", body ?? new PlayRequest(1.0), ct));
        ops.MapPost("/replay", (IHttpClientFactory http, CancellationToken ct) =>
            Forward(http, HttpMethod.Post, "/sim/replay", new { file = "recordings/demo_match.jsonl", speed = 1.0 }, ct));
        ops.MapPost("/producers/{id:int}/down", (int id, string? mode, int? seconds, IHttpClientFactory http, CancellationToken ct) =>
            Forward(http, HttpMethod.Post,
                $"/sim/producers/{id}/down?mode={(mode == "unsubscribed" ? "unsubscribed" : "silent")}&seconds={Math.Clamp(seconds ?? 40, 1, 600)}",
                null, ct));
        ops.MapPost("/producers/{id:int}/up", (int id, IHttpClientFactory http, CancellationToken ct) =>
            Forward(http, HttpMethod.Post, $"/sim/producers/{id}/up", null, ct));
    }

    public sealed record PlayRequest(double Speed);

    private static async Task<IResult> Forward(IHttpClientFactory http, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        try
        {
            using var response = await http.CreateClient("simulator").SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem($"Simulator unreachable: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
