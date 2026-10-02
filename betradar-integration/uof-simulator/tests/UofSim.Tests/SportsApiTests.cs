using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using UofSim.Core.Messages;

namespace UofSim.Tests;

public class SportsApiTests(SimFactory factory) : IClassFixture<SimFactory>
{
    private static readonly XNamespace Ns = "http://schemas.sportradar.com/sportsapi/v1/unified";

    private async Task<XElement> GetXmlAsync(string path)
    {
        var response = await factory.CreateApiClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
    }

    [Fact]
    public async Task Sports_and_tournaments_come_from_the_catalog()
    {
        var sports = await GetXmlAsync("/v1/sports/en/sports.xml");
        Assert.Equal(Ns + "sports", sports.Name);
        Assert.Equal("Soccer", (string?)sports.Element(Ns + "sport")!.Attribute("name"));

        var tournaments = await GetXmlAsync("/v1/sports/en/tournaments.xml");
        var tournament = tournaments.Element(Ns + "tournament")!;
        Assert.Equal("sr:tournament:900001", (string?)tournament.Attribute("id"));
        Assert.NotNull(tournament.Element(Ns + "current_season"));

        Assert.NotNull((await GetXmlAsync("/v1/sports/en/sports/sr:sport:1/categories.xml")).Element(Ns + "categories"));
        Assert.NotNull((await GetXmlAsync("/v1/sports/en/sports/sr:sport:1/tournaments.xml")).Element(Ns + "tournaments"));
    }

    [Fact]
    public async Task Fixture_lists_home_and_away_competitors()
    {
        var root = await GetXmlAsync("/v1/sports/en/sport_events/sr:match:900000001/fixture.xml");
        var competitors = root.Element(Ns + "fixture")!.Element(Ns + "competitors")!.Elements(Ns + "competitor").ToList();

        Assert.Equal(["home", "away"], competitors.Select(c => (string)c.Attribute("qualifier")!));
        Assert.Equal("Tbilisi Lions", (string?)competitors[0].Attribute("name"));
    }

    [Fact]
    public async Task Todays_schedule_contains_catalog_events()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var root = await GetXmlAsync($"/v1/sports/en/schedules/{today}/schedule.xml");
        Assert.Contains(root.Elements(Ns + "sport_event"), e => (string?)e.Attribute("id") == "sr:match:900000001");
    }

    [Fact]
    public async Task Unknown_entities_return_not_found()
    {
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/sports/en/sport_events/sr:match:1/summary.xml")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/sports/en/competitors/sr:competitor:1/profile.xml")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/v1/sports/en/schedules/yesterday/schedule.xml")).StatusCode);
    }

    [Fact]
    public async Task Sports_api_requires_token()
    {
        var response = await factory.CreateApiClient(token: null).GetAsync("/v1/sports/en/sports.xml");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Summary_follows_the_published_feed()
    {
        await using var sim = new SimFactory();
        var client = sim.CreateApiClient();

        var before = XDocument.Parse(await client.GetStringAsync("/v1/sports/en/sport_events/sr:match:900000001/summary.xml")).Root!;
        Assert.Equal("not_started", (string?)before.Element(Ns + "sport_event_status")!.Attribute("status"));

        await client.PostAsJsonAsync("/sim/replay", new { file = "recordings/demo_match.jsonl", speed = 1000.0 });
        await sim.Publisher.WaitForAsync(m => m.Body.Contains("<bet_settlement"));

        var after = XDocument.Parse(await client.GetStringAsync("/v1/sports/en/sport_events/sr:match:900000001/summary.xml")).Root!;
        var status = after.Element(Ns + "sport_event_status")!;
        Assert.Equal("ended", (string?)status.Attribute("status"));
        Assert.Equal("1", (string?)status.Attribute("home_score"));
        Assert.Equal("100", (string?)status.Attribute("match_status_code"));
    }

    public static TheoryData<string, string> SchemaCases() => new()
    {
        { "/v1/sports/en/sports.xml", "sports.xsd" },
        { "/v1/sports/en/tournaments.xml", "tournaments.xsd" },
        { "/v1/sports/en/sports/sr:sport:1/categories.xml", "sport_categories.xsd" },
        { "/v1/sports/en/sports/sr:sport:1/tournaments.xml", "sport_tournaments.xsd" },
        { "/v1/sports/en/schedules/live/schedule.xml", "schedule.xsd" },
        { "/v1/sports/en/sport_events/sr:match:900000001/summary.xml", "match_summary.xsd" },
        { "/v1/sports/en/sport_events/sr:match:900000001/fixture.xml", "fixtures_fixture.xsd" },
        { "/v1/sports/en/competitors/sr:competitor:900001/profile.xml", "competitor_profile.xsd" },
    };

    [SportsApiXsdTheory]
    [MemberData(nameof(SchemaCases))]
    public async Task Responses_are_schema_valid(string path, string schema)
    {
        var validator = XsdValidator.TryLoad(SportsApiXsdTheoryAttribute.XsdDir, schema)!;
        var body = await (await factory.CreateApiClient().GetAsync(path)).Content.ReadAsByteArrayAsync();

        // The published XSD declares sportEvent/additional_parents as mandatory although real responses
        // omit it (the SDK treats it as optional), so that single rule is ignored.
        var errors = validator.Validate(body).Where(e => !e.Contains("additional_parents")).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors));
    }
}

/// <summary>Runs when UOF_SPORTSAPI_XSD_DIR points at a local copy of the unified Sports API endpoint XSDs.</summary>
public sealed class SportsApiXsdTheoryAttribute : TheoryAttribute
{
    public static string? XsdDir => Environment.GetEnvironmentVariable("UOF_SPORTSAPI_XSD_DIR");

    public SportsApiXsdTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(XsdDir))
        {
            Skip = "UOF_SPORTSAPI_XSD_DIR not set";
        }
    }
}
