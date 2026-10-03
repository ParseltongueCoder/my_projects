using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Platform.Canonical.Feed;
using Platform.Canonical.Store;
using UofSim.Core.Messages;
using UofSim.Core.Scenarios;

namespace Platform.Tests;

/// <summary>A database with one played-out match (derby scenario) and the admin API on top of it.</summary>
public sealed class AdminApiFixture : IAsyncLifetime
{
    public const string EventUrn = "sr:match:900000002";
    private readonly TestDatabase _db = new();
    private WebApplicationFactory<Program>? _factory;

    public long EventId { get; private set; }

    public CanonicalStore Store { get; private set; } = null!;

    public HttpClient Client(bool authEnabled = false) => Factory(authEnabled).CreateClient();

    private WebApplicationFactory<Program> Factory(bool authEnabled)
    {
        var connectionString = _db.ConnectionString;
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Platform", connectionString);
            b.UseSetting("Auth:Enabled", authEnabled ? "true" : "false");
            b.UseSetting("Auth:Authority", "http://localhost:1/realms/none");
        });
        if (!authEnabled)
        {
            _factory = factory;
        }
        return factory;
    }

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (TestDatabase.ServerConnectionString is null)
        {
            return;
        }
        var store = Store = new CanonicalStore(_db.DataSource);
        await store.SeedMarketDescriptionsAsync([
            new MarketDescriptionRef(1, "1x2", ["all"], [], [new("1", "{$competitor1}"), new("2", "draw"), new("3", "{$competitor2}")]),
            new MarketDescriptionRef(18, "Total", ["all"], [new("total", "decimal")], [new("12", "over {total}"), new("13", "under {total}")]),
        ]);
        await store.SetProducerStateAsync(1, "LO", "up", null);
        await store.SetProducerStateAsync(3, "Ctrl", "up", null);
        EventId = await store.EnsureEventAsync(new EventRef(EventUrn, "sr:sport:1", "Soccer", "sr:category:900001", "Georgia (Sim)",
            "GEO", "sr:tournament:900001", "Sim Premier League", DateTimeOffset.UtcNow,
            [new("sr:competitor:900003", "Kutaisi Eagles", "KTE", "GEO", "home"), new("sr:competitor:900004", "Rustavi Steel", "RST", "GEO", "away")]));

        var scenario = Scenario.Parse("""
            event: sr:match:900000002
            steps:
              - { at: 0s, action: prematch_odds }
              - { at: 1s, action: kickoff }
              - { at: 2s, action: bet_stop }
              - { at: 3s, action: goal, team: home, minute: 30 }
              - { at: 4s, action: full_time }
              - { at: 5s, action: settle, certainty: 1 }
              - { at: 6s, action: settle, certainty: 2 }
            """);
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10_000;
        foreach (var (m, i) in ScenarioCompiler.Compile(scenario, 1).Select((m, i) => (m, i)))
        {
            var xml = System.Text.Encoding.UTF8.GetString(FeedMessageBuilder.WithTimestamp(m.Body, start + i * 100));
            await store.ApplyAsync(UofFeedParser.Parse(xml), EventId, xml, m.RoutingKey);
        }
        var broken = """<bet_settlement product="1" event_id="sr:match:900000002" timestamp="1" certainty="7"><outcomes><market id="1"><outcome id="1" result="1"/></market></outcomes></bet_settlement>""";
        await store.ApplyAsync(UofFeedParser.Parse(broken), EventId, broken);
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        NpgsqlConnection.ClearAllPools();
        await _db.DisposeAsync();
    }
}

public class AdminApiTests(AdminApiFixture fx) : IClassFixture<AdminApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<JsonElement> GetAsync(string path)
    {
        var response = await fx.Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    [DbFact]
    public async Task Overview_counts_and_producers()
    {
        var o = await GetAsync("/api/overview");
        Assert.Equal(2, o.GetProperty("producers").GetArrayLength());
        Assert.Equal(1, o.GetProperty("failedLastHour").GetInt64());
        Assert.True(o.GetProperty("messagesLastHour").GetInt64() >= 8);
    }

    [DbFact]
    public async Task Events_list_filters_by_status_and_text()
    {
        var all = await GetAsync("/api/events");
        var ev = all.GetProperty("items")[0];
        Assert.Equal("Kutaisi Eagles v Rustavi Steel", ev.GetProperty("name").GetString());
        Assert.Equal("ended", ev.GetProperty("status").GetString());
        Assert.Equal(1m, ev.GetProperty("homeScore").GetDecimal());

        Assert.Equal(1, (await GetAsync("/api/events?q=rustavi")).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await GetAsync("/api/events?status=live")).GetProperty("items").GetArrayLength());
    }

    [DbFact]
    public async Task Event_detail_renders_market_and_outcome_names()
    {
        var detail = await GetAsync($"/api/events/{fx.EventId}");
        var markets = detail.GetProperty("markets").EnumerateArray().ToList();
        var oneXTwo = markets.Single(m => m.GetProperty("marketTypeId").GetString() == "1");
        var total = markets.Single(m => m.GetProperty("marketTypeId").GetString() == "18");

        Assert.Equal("settled", oneXTwo.GetProperty("status").GetString());
        Assert.Equal("Kutaisi Eagles", oneXTwo.GetProperty("outcomes")[0].GetProperty("name").GetString());
        Assert.Equal("won", oneXTwo.GetProperty("outcomes")[0].GetProperty("result").GetString());
        Assert.Equal("over 2.5", total.GetProperty("outcomes")[0].GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await fx.Client().GetAsync("/api/events/999999")).StatusCode);
    }

    [DbFact]
    public async Task Settlement_history_marks_superseded_rows()
    {
        var rows = (await GetAsync($"/api/events/{fx.EventId}/settlements")).EnumerateArray().ToList();
        Assert.Equal(rows.Count / 2, rows.Count(r => r.GetProperty("state").GetString() == "superseded"));
        Assert.All(rows.Where(r => r.GetProperty("state").GetString() == "effective"),
            r => Assert.Equal(2, r.GetProperty("certainty").GetInt32()));
    }

    [DbFact]
    public async Task Bet_stops_and_messages_with_raw_payload()
    {
        Assert.Equal(1, (await GetAsync($"/api/events/{fx.EventId}/bet-stops")).GetArrayLength());

        var failed = (await GetAsync("/api/messages?status=failed")).GetProperty("items");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.False(string.IsNullOrEmpty(failed[0].GetProperty("error").GetString()));

        var page = await GetAsync($"/api/messages?eventUrn={AdminApiFixture.EventUrn}&type=odds_change&pageSize=2");
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        var id = page.GetProperty("items")[0].GetProperty("id").GetInt64();
        Assert.Equal(fx.EventId, page.GetProperty("items")[0].GetProperty("eventId").GetInt64());

        var detail = await GetAsync($"/api/messages/{id}");
        Assert.StartsWith("<?xml", detail.GetProperty("payload").GetString());
    }

    [DbFact]
    public async Task Simulator_panel_is_disabled_without_a_simulator()
    {
        var status = await GetAsync("/api/sim/status");
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Client().PostAsync("/api/sim/producers/1/down", null)).StatusCode);
    }

    [DbFact]
    public async Task Requests_without_a_token_are_rejected_when_auth_is_on()
    {
        using var client = fx.Client(authEnabled: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [DbFact]
    public async Task Stream_pushes_a_change_when_the_model_changes()
    {
        using var client = fx.Client();
        using var response = await client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await reader.ReadLineAsync());

        await Task.Delay(500); // let the background listener issue LISTEN before we change something
        await fx.Store.SetProducerStateAsync(1, "LO", "down", "producer_down");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cts.Token);
        }
        while (line is not null && !line.StartsWith("data:"));
        Assert.Contains("\"producer_status\"", line);
        await fx.Store.SetProducerStateAsync(1, "LO", "up", null);
    }
}
