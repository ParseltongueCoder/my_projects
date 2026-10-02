using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UofSim.Core.Recordings;
using UofSim.Host;
using UofSim.Host.Feed;

namespace UofSim.Tests;

public class FeedServiceTests
{
    [Fact]
    public async Task Alive_reflects_producer_mode()
    {
        var publisher = new CapturingPublisher();
        var producers = new ProducerStateStore(TimeProvider.System);
        producers.Set(1, ProducerMode.Silent);
        producers.Set(3, ProducerMode.Unsubscribed);
        var service = new AliveService(publisher, producers, TimeProvider.System,
            Options.Create(new SimOptions()), NullLogger<AliveService>.Instance);

        await service.TickAsync(CancellationToken.None);

        var alive = Assert.Single(publisher.Messages);
        Assert.Equal("-.-.-.alive.-.-.-.-", alive.RoutingKey);
        var root = XDocument.Parse(alive.Body).Root!;
        Assert.Equal("3", (string?)root.Attribute("product"));
        Assert.Equal("0", (string?)root.Attribute("subscribed"));
    }

    [Fact]
    public void Timed_down_mode_falls_back_when_it_expires()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var producers = new ProducerStateStore(clock);
        producers.Set(1, ProducerMode.Silent, TimeSpan.FromSeconds(30), then: ProducerMode.Unsubscribed);

        clock.Now += TimeSpan.FromSeconds(29);
        Assert.Equal(ProducerMode.Silent, producers.Get(1).Mode);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(ProducerMode.Unsubscribed, producers.Get(1).Mode);
    }

    [Fact]
    public async Task Replay_publishes_demo_match_with_fresh_timestamps()
    {
        await using var factory = new SimFactory();
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/sim/replay",
            new { file = "recordings/demo_match.jsonl", speed = 1000.0 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var expected = DemoMatch.Build();
        await factory.Publisher.WaitForAsync(m => m.Body.Contains("<bet_settlement"));
        var replayed = factory.Publisher.Messages.Where(m => !m.RoutingKey.StartsWith("-.-.-.")).ToList();

        Assert.Equal(expected.Select(m => m.RoutingKey), replayed.Select(m => m.RoutingKey));
        Assert.All(replayed, m =>
            Assert.True((long)XDocument.Parse(m.Body).Root!.Attribute("timestamp")! >= started));
    }

    [Fact]
    public async Task Replay_rejects_paths_outside_the_data_directory()
    {
        await using var factory = new SimFactory();
        var response = await factory.CreateClient().PostAsJsonAsync("/sim/replay", new { file = "../../appsettings.json" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Control_api_can_take_a_producer_down()
    {
        await using var factory = new SimFactory();
        var client = factory.CreateClient();

        var down = await client.PostAsync("/sim/producers/1/down?mode=unsubscribed", null);
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        Assert.Contains("Unsubscribed", await client.GetStringAsync("/sim/status"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/sim/producers/42/down", null)).StatusCode);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
