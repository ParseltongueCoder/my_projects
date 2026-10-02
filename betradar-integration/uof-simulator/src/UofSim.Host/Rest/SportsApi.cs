using System.Globalization;
using System.Xml.Linq;
using UofSim.Core.SportsApi;

namespace UofSim.Host.Rest;

/// <summary>
/// Mock of the Sports API part of the Betradar REST API (<c>/v1/sports/{lang}/...</c>): what the SDK
/// needs to resolve event, competitor and market names ({$competitor1}) and to warm its caches.
/// Content comes from <c>data/catalog.yaml</c> plus the live state of published odds_change messages.
/// </summary>
public static class SportsApi
{
    public static void MapSportsApi(this WebApplication app)
    {
        var sports = app.MapGroup("/v1/sports/{lang}").AddEndpointFilter(BetradarApi.RequireToken);

        sports.MapGet("/sports.xml", (SportsApiXml api, TimeProvider clock) =>
            Xml(api.Sports(clock.GetUtcNow())));
        sports.MapGet("/tournaments.xml", (SportsApiXml api, TimeProvider clock) =>
            Xml(api.Tournaments(clock.GetUtcNow())));
        sports.MapGet("/sports/{sportUrn}/tournaments.xml", (string sportUrn, SportsApiXml api, TimeProvider clock) =>
            Xml(api.SportTournaments(sportUrn, clock.GetUtcNow()), $"sport {sportUrn}"));
        sports.MapGet("/sports/{sportUrn}/categories.xml", (string sportUrn, SportsApiXml api, TimeProvider clock) =>
            Xml(api.SportCategories(sportUrn, clock.GetUtcNow()), $"sport {sportUrn}"));

        sports.MapGet("/schedules/live/schedule.xml", (SportsApiXml api, TimeProvider clock) =>
            Xml(api.Schedule(api.LiveEvents, clock.GetUtcNow())));
        sports.MapGet("/schedules/pre/schedule.xml", (int? start, int? limit, SportsApiXml api, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            var page = api.UpcomingEvents(now).Skip(Math.Max(0, start ?? 0)).Take(Math.Clamp(limit ?? 1000, 1, 1000));
            return Xml(api.Schedule(page, now));
        });
        sports.MapGet("/schedules/{date}/schedule.xml", (string date, SportsApiXml api, TimeProvider clock) =>
            DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? Xml(api.Schedule(api.EventsOn(day), clock.GetUtcNow()))
                : BetradarApi.Response(StatusCodes.Status400BadRequest, "BAD_REQUEST", $"Invalid date '{date}'"));

        sports.MapGet("/sport_events/{eventUrn}/summary.xml", (string eventUrn, SportsApiXml api, TimeProvider clock) =>
            Xml(api.Summary(eventUrn, clock.GetUtcNow()), $"event {eventUrn}"));
        sports.MapGet("/sport_events/{eventUrn}/fixture.xml", (string eventUrn, SportsApiXml api, TimeProvider clock) =>
            Xml(api.Fixture(eventUrn, clock.GetUtcNow()), $"event {eventUrn}"));
        // Same content; the SDK uses this path to bypass caches after a fixture_change.
        sports.MapGet("/sport_events/{eventUrn}/fixture_change_fixture.xml", (string eventUrn, SportsApiXml api, TimeProvider clock) =>
            Xml(api.Fixture(eventUrn, clock.GetUtcNow()), $"event {eventUrn}"));
        sports.MapGet("/competitors/{competitorUrn}/profile.xml", (string competitorUrn, SportsApiXml api, TimeProvider clock) =>
            Xml(api.CompetitorProfile(competitorUrn, clock.GetUtcNow()), $"competitor {competitorUrn}"));
    }

    private static IResult Xml(XElement? root, string? what = null) =>
        root is null
            ? BetradarApi.Response(StatusCodes.Status404NotFound, "NOT_FOUND", $"Unknown {what}")
            : BetradarApi.XmlResult(root);
}
