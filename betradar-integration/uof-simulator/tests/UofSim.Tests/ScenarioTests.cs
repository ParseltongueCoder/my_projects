using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using UofSim.Core.Messages;
using UofSim.Core.Odds;
using UofSim.Core.Recordings;
using UofSim.Core.Scenarios;

namespace UofSim.Tests;

public class ScenarioTests
{
    private static readonly string ScenarioDir = Path.Combine(AppContext.BaseDirectory, "data", "scenarios");

    private static IReadOnlyList<RecordedMessage> CompileDerby() =>
        ScenarioCompiler.Compile(Scenario.Load(Path.Combine(ScenarioDir, "derby_settlement_rollback.yaml")), 1);

    private static XElement Root(RecordedMessage m) => XDocument.Parse(m.Body).Root!;

    [Fact]
    public void Poisson_probabilities_are_consistent()
    {
        var p = PoissonModel.Evaluate(1.6, 1.0, minute: 0, homeScore: 0, awayScore: 0);
        Assert.Equal(1.0, p.Home + p.Draw + p.Away, 6);
        Assert.True(p.Home > p.Away, "stronger home team is favourite");

        var late = PoissonModel.Evaluate(1.6, 1.0, minute: 88, homeScore: 0, awayScore: 1);
        Assert.True(late.Away > 0.9, "a late lead is almost decisive");
        Assert.Equal(1.0, PoissonModel.Evaluate(1, 1, 90, 2, 1).Over25, 6);
    }

    [Fact]
    public void Margin_makes_the_book_overround()
    {
        var p = PoissonModel.Evaluate(1.4, 1.1, 0, 0, 0);
        var overround = 1 / PoissonModel.ToOdds(p.Home) + 1 / PoissonModel.ToOdds(p.Draw) + 1 / PoissonModel.ToOdds(p.Away);
        Assert.InRange(overround, 1.03, 1.07);
    }

    [Fact]
    public void Derby_compiles_to_the_expected_message_sequence()
    {
        var types = CompileDerby().Select(m => Root(m).Name.LocalName).ToList();

        Assert.Equal("odds_change", types[0]);
        Assert.Equal(3, types.Count(t => t == "bet_stop"));
        Assert.Equal(["bet_settlement", "rollback_bet_settlement", "bet_settlement"], types.TakeLast(3));
    }

    [Fact]
    public void Prematch_comes_from_ctrl_and_live_from_live_odds()
    {
        var messages = CompileDerby();
        Assert.Equal("hi.pre.-.odds_change.1.sr:match.900000002.-", messages[0].RoutingKey);
        Assert.Equal("3", (string?)Root(messages[0]).Attribute("product"));
        Assert.All(messages.Skip(1), m => Assert.Equal("1", (string?)Root(m).Attribute("product")));
    }

    [Fact]
    public void Score_and_clock_follow_the_goals()
    {
        var lastLive = CompileDerby().Last(m => m.Body.Contains("<clock"));
        var status = Root(lastLive).Element("sport_event_status")!;
        Assert.Equal("2", (string?)status.Attribute("home_score"));
        Assert.Equal("1", (string?)status.Attribute("away_score"));
        Assert.Equal("88:00", (string?)status.Element("clock")!.Attribute("match_time"));
    }

    [Fact]
    public void Settlement_matches_the_final_score()
    {
        var settlement = Root(CompileDerby().Last());
        Assert.Equal("2", (string?)settlement.Attribute("certainty"));

        string Result(int market, string outcome) =>
            (string)settlement.Descendants("market").Single(m => (int)m.Attribute("id")! == market)
                .Elements("outcome").Single(o => (string?)o.Attribute("id") == outcome).Attribute("result")!;

        Assert.Equal("1", Result(1, "1"));   // 2-1 home win
        Assert.Equal("1", Result(18, "12")); // 3 goals: over 2.5
        Assert.Equal("1", Result(29, "74")); // both teams scored
        Assert.Equal("0", Result(1, "3"));
    }

    [Fact]
    public void Halftime_suspends_and_full_time_deactivates_markets()
    {
        var messages = CompileDerby().Select(Root).Where(r => r.Name == "odds_change").ToList();
        var halftime = messages.First(r => (string?)r.Element("sport_event_status")!.Attribute("match_status") == "31");
        var fullTime = messages.Last();

        Assert.All(halftime.Descendants("market"), m => Assert.Equal("-1", (string?)m.Attribute("status")));
        Assert.All(fullTime.Descendants("market"), m => Assert.Equal("0", (string?)m.Attribute("status")));
        Assert.Empty(fullTime.Descendants("outcome"));
    }

    [Fact]
    public void Abandoned_match_cancels_markets()
    {
        var messages = ScenarioCompiler.Compile(Scenario.Load(Path.Combine(ScenarioDir, "abandoned_match.yaml")), 1);
        var cancel = Root(messages.Last());
        Assert.Equal("bet_cancel", cancel.Name.LocalName);
        Assert.Equal(3, cancel.Elements("market").Count());
    }

    [Theory]
    [InlineData("event: sr:match:1\nsteps:\n  - { at: 1s, action: fly }", "Unknown action")]
    [InlineData("event: sr:match:1\nsteps:\n  - { at: soon, action: kickoff }", "Invalid time")]
    [InlineData("steps: []", "needs an 'event'")]
    public void Invalid_scenarios_are_rejected(string yaml, string message)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Scenario.Parse(yaml));
        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("15s", 15_000)]
    [InlineData("1.5s", 1_500)]
    [InlineData("2m", 120_000)]
    public void Durations_are_parsed(string at, long expected) =>
        Assert.Equal(expected, new ScenarioStep { At = at }.OffsetMs);

    [XsdFact]
    public void All_scenario_messages_are_schema_valid()
    {
        var validator = XsdValidator.TryLoad(XsdFactAttribute.XsdDir, XsdValidator.FeedSchema)!;
        foreach (var file in Directory.GetFiles(ScenarioDir, "*.yaml"))
        {
            foreach (var m in ScenarioCompiler.Compile(Scenario.Load(file), 1))
            {
                var errors = validator.Validate(Encoding.UTF8.GetBytes(m.Body));
                Assert.True(errors.Count == 0, $"{Path.GetFileName(file)} {m.RoutingKey}: {string.Join("; ", errors)}");
            }
        }
    }

    [Fact]
    public async Task Scenario_runs_through_the_control_api_and_can_be_recorded()
    {
        await using var sim = new SimFactory();
        var client = sim.CreateClient();
        var recording = $"recordings/test-{Guid.NewGuid():N}.jsonl";

        Assert.Contains("derby_settlement_rollback", await client.GetStringAsync("/sim/scenarios"));
        (await client.PostAsJsonAsync("/sim/recorder/start", new { file = recording })).EnsureSuccessStatusCode();

        var start = await client.PostAsJsonAsync("/sim/scenarios/derby_settlement_rollback", new { speed = 1000.0 });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        await sim.Publisher.WaitForAsync(m => m.Body.Contains("certainty=\"2\""));

        (await client.PostAsync("/sim/recorder/stop", null)).EnsureSuccessStatusCode();
        var recorded = Recording.Read(Path.Combine(sim.DataDir, recording));
        File.Delete(Path.Combine(sim.DataDir, recording));

        Assert.Equal(CompileDerby().Select(m => m.RoutingKey), recorded.Select(m => m.RoutingKey));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/sim/scenarios/..%2Fcatalog", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/sim/scenarios/missing", new { })).StatusCode);
    }
}
