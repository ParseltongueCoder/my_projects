using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using UofSim.Core.Messages;
using UofSim.Core.Producers;
using UofSim.Host.Feed;

namespace UofSim.Host.Rest;

/// <summary>
/// Mock of the Betradar REST API endpoints the official SDK calls at startup and during recovery
/// (see docs/03-feed-simulator.md §11). Paths match the real API so only the host changes.
/// </summary>
public static partial class BetradarApi
{
    private const string Xml = "application/xml; charset=utf-8";

    public static void MapBetradarApi(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1").AddEndpointFilter(RequireToken);

        v1.MapGet("/users/whoami.xml", (IOptions<SimOptions> o, TimeProvider clock) =>
            XmlResult(new XElement("bookmaker_details",
                new XAttribute("response_code", "OK"),
                new XAttribute("expire_at", clock.GetUtcNow().AddYears(1).ToString("yyyy-MM-ddTHH:mm:ssZ")),
                new XAttribute("bookmaker_id", o.Value.BookmakerId),
                new XAttribute("virtual_host", o.Value.VirtualHost))));

        v1.MapGet("/descriptions/producers.xml", (HttpRequest request) =>
            XmlResult(new XElement("producers",
                new XAttribute("response_code", "OK"),
                Producers.All.Select(p => new XElement("producer",
                    new XAttribute("id", p.Id),
                    new XAttribute("name", p.Name),
                    new XAttribute("description", p.Description),
                    // The SDK appends "recovery/initiate_request..." to api_url, so it must end with '/'.
                    // Using the request's own host keeps it valid whatever host/port the client configured.
                    new XAttribute("api_url", $"{request.Scheme}://{request.Host}/v1/{p.UrlCode}/"),
                    new XAttribute("active", true),
                    new XAttribute("scope", p.Scope),
                    new XAttribute("stateful_recovery_window_in_minutes", p.StatefulRecoveryWindowMinutes))))));

        v1.MapGet("/descriptions/{lang}/markets.xml", (string lang, IOptions<SimOptions> o) =>
            StaticDescription(o.Value, "markets", lang));
        v1.MapGet("/descriptions/{lang}/variants.xml", (string lang, IOptions<SimOptions> o) =>
            StaticDescription(o.Value, "variants", lang));
        v1.MapGet("/descriptions/{lang}/match_status.xml", (string lang, IOptions<SimOptions> o) =>
            StaticDescription(o.Value, "match_status", lang));
        v1.MapGet("/descriptions/betstop_reasons.xml", (IOptions<SimOptions> o) =>
            StaticDescription(o.Value, "betstop_reasons", null));
        v1.MapGet("/descriptions/betting_status.xml", (IOptions<SimOptions> o) =>
            StaticDescription(o.Value, "betting_status", null));
        v1.MapGet("/descriptions/void_reasons.xml", (IOptions<SimOptions> o) =>
            StaticDescription(o.Value, "void_reasons", null));
        // Single variant market descriptions arrive with the L3 engine (S4); the real path has no ".xml".
        v1.MapGet("/descriptions/{lang}/markets/{id:int}/variants/{variant}", (int id, string variant) =>
            Response(StatusCodes.Status404NotFound, "NOT_FOUND", $"Variant {variant} of market {id} is not simulated yet"));

        v1.MapPost("/{product}/recovery/initiate_request",
            (string product, long? request_id, long? after, int? node_id, RecoveryService recovery) =>
                AcceptRecovery(product, request_id, after, node_id, null, recovery));
        v1.MapPost("/{product}/odds/events/{eventUrn}/initiate_request",
            (string product, string eventUrn, long? request_id, int? node_id, RecoveryService recovery) =>
                AcceptRecovery(product, request_id, null, node_id, eventUrn, recovery));
        v1.MapPost("/{product}/stateful_messages/events/{eventUrn}/initiate_request",
            (string product, string eventUrn, long? request_id, int? node_id, RecoveryService recovery) =>
                AcceptRecovery(product, request_id, null, node_id, eventUrn, recovery));
    }

    private static async ValueTask<object?> RequireToken(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var expected = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<SimOptions>>().Value.AccessToken;
        var actual = ctx.HttpContext.Request.Headers["x-access-token"].ToString();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return Response(StatusCodes.Status403Forbidden, "FORBIDDEN", "Invalid or missing x-access-token");
        }
        return await next(ctx);
    }

    private static IResult AcceptRecovery(
        string product, long? requestId, long? after, int? nodeId, string? eventUrn, RecoveryService recovery)
    {
        var producer = Producers.ByUrlCode(product);
        if (producer is null)
        {
            return Response(StatusCodes.Status404NotFound, "NOT_FOUND", $"Unknown product '{product}'");
        }
        if (requestId is null)
        {
            return Response(StatusCodes.Status400BadRequest, "BAD_REQUEST", "request_id is required");
        }

        recovery.Enqueue(new RecoveryRequest(producer.Id, requestId.Value, nodeId, after, eventUrn));
        return Response(StatusCodes.Status202Accepted, "ACCEPTED",
            $"Request for {producer.Name} recovery accepted (request_id={requestId})");
    }

    private static IResult StaticDescription(SimOptions options, string name, string? lang)
    {
        var dir = Path.Combine(options.DataDir, "static", "descriptions");
        // Only the language suffix comes from the URL; anything that is not a language code falls back to English.
        var localized = lang is not null && LanguageCode().IsMatch(lang) ? Path.Combine(dir, $"{name}.{lang}.xml") : null;
        var candidates = lang is null
            ? [Path.Combine(dir, $"{name}.xml")]
            : new[] { localized, Path.Combine(dir, $"{name}.en.xml") };

        var path = candidates.FirstOrDefault(p => p is not null && File.Exists(p));
        return path is null
            ? Response(StatusCodes.Status404NotFound, "NOT_FOUND", $"No description file for '{name}'")
            : Results.File(path, Xml);
    }

    internal static IResult Response(int statusCode, string responseCode, string message) =>
        XmlResult(new XElement("response",
            new XAttribute("response_code", responseCode),
            new XElement("action", message),
            new XElement("message", message)), statusCode);

    private static IResult XmlResult(XElement root, int statusCode = StatusCodes.Status200OK) =>
        Results.Text(Encoding.UTF8.GetString(FeedMessageBuilder.Serialize(root)), Xml, Encoding.UTF8, statusCode);

    [GeneratedRegex("^[a-z]{2}(-[a-z]{2})?$")]
    private static partial Regex LanguageCode();
}
