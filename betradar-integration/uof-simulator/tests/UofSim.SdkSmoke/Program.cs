// SDK smoke test: runs the official Sportradar .NET SDK against the simulator and checks that
//   1. the SDK starts (whoami, producers, descriptions) and both producers go UP after recovery,
//   2. a replayed match reaches the SDK as odds_change / bet_stop / bet_settlement,
//   3. market and outcome names resolve through the mock Sports API ("{$competitor1}" → team name),
//   4. a YAML scenario delivers a settlement rollback,
//   5. (--chaos) a silent producer is reported DOWN and comes back UP via recovery.
// Exit code 0 = pass. Requires `docker compose up` (or the simulator + RabbitMQ running locally).

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sportradar.OddsFeed.SDK.Api;
using Sportradar.OddsFeed.SDK.Api.Config;
using Sportradar.OddsFeed.SDK.Api.EventArguments;
using Sportradar.OddsFeed.SDK.Common.Extensions;
using Sportradar.OddsFeed.SDK.Entities;
using Sportradar.OddsFeed.SDK.Entities.Rest;

var apiHost = Env("UOF_API_HOST", "localhost:8080");
var amqpHost = Env("UOF_AMQP_HOST", "localhost");
var token = Env("UOF_ACCESS_TOKEN", "sim-token-0000000000");
var chaos = args.Contains("--chaos");
var timeout = TimeSpan.FromSeconds(int.Parse(Env("SMOKE_TIMEOUT_SECONDS", "120"), CultureInfo.InvariantCulture));

var config = UofSdk.GetConfigurationBuilder()
    .SetAccessToken(token)
    .SelectCustom()
    .SetApiHost(apiHost)
    .UseApiSsl(false)
    .SetMessagingHost(amqpHost)
    .SetMessagingPort(int.Parse(Env("UOF_AMQP_PORT", "5671"), CultureInfo.InvariantCulture))
    .UseMessagingSsl(true)
    .SetMessagingUsername(token)
    .SetMessagingPassword(Env("UOF_AMQP_PASSWORD", "sim"))
    .SetNodeId(1)
    .SetDefaultLanguage(CultureInfo.GetCultureInfo("en"))
    .Build();

using var host = Host.CreateDefaultBuilder()
    .ConfigureLogging(l => l.ClearProviders().AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning))
    .ConfigureServices(s => s.AddUofSdk(config))
    .Build();

var sdk = new UofSdk(host.Services);
var session = sdk.GetSessionBuilder().SetMessageInterest(MessageInterest.AllMessages).Build();

var up = new ConcurrentDictionary<int, bool>();
var received = new ConcurrentQueue<string>();
var downSeen = new TaskCompletionSource();
var settlementSeen = new TaskCompletionSource();
var rollbackSeen = new TaskCompletionSource();
var liveMarkets = new TaskCompletionSource<IReadOnlyList<IMarketWithOdds>>();
var en = CultureInfo.GetCultureInfo("en");

sdk.ProducerUp += (_, e) =>
{
    var producer = e.GetProducerStatusChange().Producer;
    Log($"ProducerUp {producer.Id} ({producer.Name})");
    up[producer.Id] = true;
};
sdk.ProducerDown += (_, e) =>
{
    var producer = e.GetProducerStatusChange().Producer;
    Log($"ProducerDown {producer.Id} ({producer.Name})");
    up[producer.Id] = false;
    downSeen.TrySetResult();
};
session.OnOddsChange += (_, e) =>
{
    var m = e.GetOddsChange();
    received.Enqueue("odds_change");
    Log($"odds_change {m.Event.Id} producer={m.Producer.Id} markets={m.Markets.Count()}");
    if (m.Event.Id.ToString() == "sr:match:900000001" && m.Producer.Id == 1)
    {
        liveMarkets.TrySetResult(m.Markets.ToList());
    }
};
session.OnBetStop += (_, e) =>
{
    received.Enqueue("bet_stop");
    Log($"bet_stop {e.GetBetStop().Event.Id}");
};
session.OnBetSettlement += (_, e) =>
{
    var m = e.GetBetSettlement();
    received.Enqueue("bet_settlement");
    Log($"bet_settlement {m.Event.Id} markets={m.Markets.Count()}");
    settlementSeen.TrySetResult();
};
session.OnRollbackBetSettlement += (_, e) =>
{
    Log($"rollback_bet_settlement {e.GetBetSettlementRollback().Event.Id}");
    rollbackSeen.TrySetResult();
};
session.OnUnparsableMessageReceived += (_, e) => Log($"UNPARSABLE {e.MessageType}");

using var control = new HttpClient { BaseAddress = new Uri($"http://{apiHost}") };

try
{
    sdk.Open();
    Log("SDK opened");

    await WaitUntil(() => up.Count == 2 && up.Values.All(v => v), "both producers UP");

    var replay = await control.PostAsJsonAsync("/sim/replay", new { file = "recordings/demo_match.jsonl", speed = 10.0 });
    replay.EnsureSuccessStatusCode();
    await settlementSeen.Task.WaitAsync(timeout);
    Check(received.Contains("odds_change") && received.Contains("bet_stop"), "odds_change and bet_stop received");

    var markets = await liveMarkets.Task.WaitAsync(timeout);
    var oneXTwo = markets.Single(m => m.Id == 1);
    var total = markets.Single(m => m.Id == 18);
    var homeName = await oneXTwo.OutcomeOdds.Single(o => o.Id == "1").GetNameAsync(en);
    var overName = await total.OutcomeOdds.Single(o => o.Id == "12").GetNameAsync(en);
    Log($"names: '{await oneXTwo.GetNameAsync(en)}' home='{homeName}', '{await total.GetNameAsync(en)}' over='{overName}'");
    Check(homeName == "Tbilisi Lions", "outcome {$competitor1} resolved to the home team");
    Check(overName == "over 2.5", "outcome {total} specifier resolved");

    var scenario = await control.PostAsJsonAsync("/sim/scenarios/derby_settlement_rollback", new { speed = 10.0 });
    scenario.EnsureSuccessStatusCode();
    await rollbackSeen.Task.WaitAsync(timeout);

    if (chaos)
    {
        (await control.PostAsync("/sim/producers/1/down?mode=silent&seconds=40", null)).EnsureSuccessStatusCode();
        await downSeen.Task.WaitAsync(timeout);
        await WaitUntil(() => up.GetValueOrDefault(1), "producer 1 UP again after recovery");
    }

    Log("SMOKE TEST PASSED");
    return 0;
}
catch (Exception ex)
{
    Log($"SMOKE TEST FAILED: {ex.Message}");
    return 1;
}
finally
{
    sdk.Close();
}

async Task WaitUntil(Func<bool> condition, string what)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition())
    {
        if (DateTime.UtcNow > deadline)
        {
            throw new TimeoutException($"timed out waiting for {what}");
        }
        await Task.Delay(250);
    }
    Log($"OK: {what}");
}

static void Check(bool condition, string what)
{
    if (!condition)
    {
        throw new InvalidOperationException($"check failed: {what}");
    }
    Log($"OK: {what}");
}

static void Log(string message) => Console.WriteLine($"[smoke {DateTime.UtcNow:HH:mm:ss}] {message}");

static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
