using System.Globalization;
using System.Text.Json;
using Offer.Core;

namespace Offer.Tests;

/// <summary>Golden cases (Golden/pricing.json): feed fixture + settings → expected offer, compared exactly.</summary>
public sealed class PricingGoldenTests
{
    private static readonly JsonElement Root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "pricing.json"))).RootElement;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Root.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden(string name)
    {
        var c = Root.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var now = DateTime.Parse(Root.GetProperty("now").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var result = OfferPricer.Price(Market(c.GetProperty("market")), Settings(c.GetProperty("settings")),
            c.TryGetProperty("gate", out var g) ? Gate(g) : new MarketGate(), Overrides(c), now);

        var expect = c.GetProperty("expect");
        if (expect.TryGetProperty("status", out var status))
        {
            Assert.Equal(status.GetString(), result.Status.ToString());
        }
        if (expect.TryGetProperty("mode", out var mode))
        {
            Assert.Equal(mode.GetString(), result.ModeUsed);
        }
        if (expect.TryGetProperty("complete", out var complete))
        {
            Assert.Equal(complete.GetBoolean(), result.Complete);
        }
        if (expect.TryGetProperty("feedOverround", out var overround))
        {
            Assert.Equal(overround.GetDecimal(), result.FeedOverround);
        }
        if (expect.TryGetProperty("reasons", out var reasons))
        {
            Assert.Equal(reasons.EnumerateArray().Select(r => r.GetString()!), result.Reasons);
        }
        Check(expect, "odds", result, (o, v) => Assert.Equal(v.ValueKind == JsonValueKind.Null ? null : v.GetDecimal(), o.Odds));
        Check(expect, "sources", result, (o, v) => Assert.Equal(v.GetString(), o.Source));
        Check(expect, "hidden", result, (o, v) => Assert.Equal(v.GetString(), o.HiddenReason));
    }

    private static void Check(JsonElement expect, string property, PricedMarket result, Action<PricedOutcome, JsonElement> assert)
    {
        if (!expect.TryGetProperty(property, out var map))
        {
            return;
        }
        foreach (var p in map.EnumerateObject())
        {
            assert(result.Outcomes.Single(o => o.Code == p.Name), p.Value);
        }
    }

    private static MarketInput Market(JsonElement m) => new(
        1,
        Str(m, "feedStatus") ?? "active",
        Str(m, "status") ?? "active",
        m.GetProperty("outcomes").EnumerateArray().Select(o => new OutcomeInput(
            o.GetProperty("code").GetString()!,
            o.GetProperty("odds").GetDecimal(),
            o.TryGetProperty("probability", out var p) ? p.GetDecimal() : null,
            !o.TryGetProperty("isActive", out var a) || a.GetBoolean())).ToList(),
        m.TryGetProperty("isManual", out var manual) && manual.GetBoolean(),
        !m.TryGetProperty("closedSet", out var closed) || closed.GetBoolean(),
        m.TryGetProperty("definedOutcomes", out var defined) ? defined.GetInt32() : null);

    private static PricingSettings Settings(JsonElement s)
    {
        var d = new PricingSettings();
        return new PricingSettings(
            Str(s, "mode") ?? d.Mode,
            Dec(s, "pct") ?? d.Pct,
            Dec(s, "deltaPct") ?? d.DeltaPct,
            Str(s, "method") ?? d.Method,
            Str(s, "removeMethod") ?? d.RemoveMethod,
            s.TryGetProperty("useFeedProbabilities", out var u) && u.GetBoolean(),
            Dec(s, "floorPct") ?? d.FloorPct,
            Dec(s, "minOdds") ?? d.MinOdds,
            Dec(s, "maxOdds") ?? d.MaxOdds,
            Str(s, "ladder") ?? d.Ladder,
            Dec(s, "overrideFeedTolerancePct") ?? d.OverrideFeedTolerancePct);
    }

    private static MarketGate Gate(JsonElement g) => new(
        Bool(g, "visible") ?? true,
        Bool(g, "marketTypeEnabled") ?? true,
        Bool(g, "phaseEnabled") ?? true,
        Bool(g, "tradingClosed") ?? false,
        Bool(g, "tradingSuspended") ?? false,
        Str(g, "producerDownPolicy") ?? "suspend",
        Bool(g, "eventLive") ?? false,
        Bool(g, "manualMarketLive") ?? false);

    private static List<OddsOverride> Overrides(JsonElement c) =>
        !c.TryGetProperty("overrides", out var list)
            ? []
            : list.EnumerateArray().Select(o => new OddsOverride(
                o.GetProperty("id").GetInt64(),
                o.GetProperty("outcomeCode").GetString()!,
                o.GetProperty("kind").GetString()!,
                o.GetProperty("value").GetDecimal(),
                DateTime.Parse(o.GetProperty("expiresAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                Str(o, "clearOn") ?? "expiry",
                Dec(o, "feedOddsAtSet"))).ToList();

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetDecimal() : null;

    private static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetBoolean() : null;
}
