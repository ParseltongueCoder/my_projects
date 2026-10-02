using System.Xml.Linq;
using UofSim.Core.Catalog;

namespace UofSim.Core.SportsApi;

/// <summary>
/// Builds Sports API (<c>/v1/sports/...</c>) responses from the synthetic catalog. Element order follows
/// the unified Sports API XSDs; everything lives in the <c>sportsapi/v1/unified</c> namespace.
/// </summary>
public sealed class SportsApiXml(SimCatalog catalog, EventStateStore states, DateTimeOffset simStart)
{
    public static readonly XNamespace Ns = "http://schemas.sportradar.com/sportsapi/v1/unified";

    public DateTimeOffset Scheduled(EventDef e) => simStart.AddMinutes(e.ScheduledOffsetMinutes);

    public XElement Sports(DateTimeOffset now) =>
        Root("sports", now, catalog.Sports.Select(Sport));

    public XElement Tournaments(DateTimeOffset now) =>
        Root("tournaments", now, catalog.Tournaments.Select(t =>
            TournamentElement("tournament", t.Sport, t.Category, t.Tournament,
                t.Tournament.Season is { } season ? SeasonElement("current_season", season, t.Tournament.Id) : null)));

    public XElement? SportTournaments(string sportUrn, DateTimeOffset now) =>
        catalog.Sports.FirstOrDefault(s => s.Id == sportUrn) is not { } sport
            ? null
            : Root("sport_tournaments", now,
                Sport(sport),
                new XElement(Ns + "tournaments", sport.Categories.SelectMany(c =>
                    c.Tournaments.Select(t => TournamentElement("tournament", sport, c, t)))));

    public XElement? SportCategories(string sportUrn, DateTimeOffset now) =>
        catalog.Sports.FirstOrDefault(s => s.Id == sportUrn) is not { } sport
            ? null
            : Root("sport_categories", now,
                Sport(sport),
                new XElement(Ns + "categories", sport.Categories.Select(Category)));

    public XElement Schedule(IEnumerable<EventDef> events, DateTimeOffset now) =>
        Root("schedule", now, events.Select(e => SportEvent("sport_event", e)));

    public IEnumerable<EventDef> EventsOn(DateOnly date) =>
        catalog.Events.Where(e => DateOnly.FromDateTime(Scheduled(e).UtcDateTime) == date);

    public IEnumerable<EventDef> LiveEvents =>
        states.LiveEvents.Select(catalog.FindEvent).OfType<EventDef>();

    public IEnumerable<EventDef> UpcomingEvents(DateTimeOffset now) =>
        catalog.Events.Where(e => states.Get(e.Id).Status == 0 && Scheduled(e) >= now.AddHours(-3))
            .OrderBy(Scheduled);

    public XElement? Summary(string eventUrn, DateTimeOffset now)
    {
        if (catalog.FindEvent(eventUrn) is not { } e)
        {
            return null;
        }
        var s = states.Get(eventUrn);
        var status = new XElement(Ns + "sport_event_status",
            s.MatchTime is not null ? new XElement(Ns + "clock", new XAttribute("match_time", s.MatchTime)) : null,
            new XAttribute("status", StatusName(s.Status)),
            new XAttribute("match_status", MatchStatusName(s.MatchStatus)),
            new XAttribute("home_score", s.HomeScore),
            new XAttribute("away_score", s.AwayScore),
            new XAttribute("status_code", s.Status),
            new XAttribute("match_status_code", s.MatchStatus));
        return Root("match_summary", now, SportEvent("sport_event", e), status);
    }

    public XElement? Fixture(string eventUrn, DateTimeOffset now) =>
        catalog.FindEvent(eventUrn) is not { } e
            ? null
            : Root("fixtures_fixture", now,
                SportEvent("fixture", e, new XAttribute("start_time_confirmed", true),
                    new XAttribute("start_time", Iso(Scheduled(e)))));

    public XElement? CompetitorProfile(string competitorUrn, DateTimeOffset now)
    {
        if (catalog.FindCompetitor(competitorUrn) is not { } c)
        {
            return null;
        }
        // A competitor belongs to the category/sport of the first tournament it plays in.
        var firstEvent = catalog.Events.FirstOrDefault(e => e.Home == c.Id || e.Away == c.Id);
        var context = firstEvent is null ? null : catalog.FindTournament(firstEvent.Tournament);
        return Root("competitor_profile", now,
            new XElement(Ns + "competitor", CompetitorAttributes(c),
                context is { } ctx ? new[] { Sport(ctx.Sport), Category(ctx.Category) } : null));
    }

    private XElement SportEvent(string elementName, EventDef e, params object[] extraAttributes)
    {
        var (sport, category, tournament) = catalog.FindTournament(e.Tournament)!.Value;
        var home = catalog.FindCompetitor(e.Home)!;
        var away = catalog.FindCompetitor(e.Away)!;
        return new XElement(Ns + elementName,
            new XAttribute("id", e.Id),
            new XAttribute("scheduled", Iso(Scheduled(e))),
            new XAttribute("start_time_tbd", false),
            new XAttribute("liveodds", "booked"),
            new XAttribute("status", StatusName(states.Get(e.Id).Status)),
            extraAttributes,
            tournament.Season is { } season ? SeasonElement("season", season, tournament.Id) : null,
            TournamentElement("tournament", sport, category, tournament),
            new XElement(Ns + "competitors",
                new XElement(Ns + "competitor", CompetitorAttributes(home), new XAttribute("qualifier", "home")),
                new XElement(Ns + "competitor", CompetitorAttributes(away), new XAttribute("qualifier", "away"))));
    }

    private static XElement TournamentElement(
        string name, SportDef sport, CategoryDef category, TournamentDef tournament, XElement? extra = null) =>
        new(Ns + name,
            new XAttribute("id", tournament.Id),
            new XAttribute("name", tournament.Name),
            Sport(sport),
            Category(category),
            extra);

    private static XElement SeasonElement(string name, SeasonDef season, string tournamentId) =>
        new(Ns + name,
            new XAttribute("id", season.Id),
            new XAttribute("name", season.Name),
            new XAttribute("start_date", season.StartDate),
            new XAttribute("end_date", season.EndDate),
            season.Year is not null ? new XAttribute("year", season.Year) : null,
            new XAttribute("tournament_id", tournamentId));

    private static XElement Sport(SportDef s) =>
        new(Ns + "sport", new XAttribute("id", s.Id), new XAttribute("name", s.Name));

    private static XElement Category(CategoryDef c) =>
        new(Ns + "category",
            new XAttribute("id", c.Id),
            new XAttribute("name", c.Name),
            c.CountryCode is not null ? new XAttribute("country_code", c.CountryCode) : null);

    private static IEnumerable<XAttribute> CompetitorAttributes(CompetitorDef c)
    {
        yield return new XAttribute("id", c.Id);
        yield return new XAttribute("name", c.Name);
        yield return new XAttribute("abbreviation", c.Abbreviation);
        if (c.Country is not null)
        {
            yield return new XAttribute("country", c.Country);
        }
        if (c.CountryCode is not null)
        {
            yield return new XAttribute("country_code", c.CountryCode);
        }
    }

    private static XElement Root(string name, DateTimeOffset now, params object?[] content) =>
        new(Ns + name, new XAttribute("generated_at", Iso(now)), content);

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss+00:00");

    internal static string StatusName(int status) => status switch
    {
        0 => "not_started",
        1 => "live",
        2 => "suspended",
        3 => "ended",
        4 => "closed",
        5 => "cancelled",
        _ => "not_started",
    };

    internal static string MatchStatusName(int matchStatus) => matchStatus switch
    {
        0 => "not_started",
        6 => "1st_half",
        7 => "2nd_half",
        31 => "halftime",
        100 => "ended",
        _ => "not_started",
    };
}
