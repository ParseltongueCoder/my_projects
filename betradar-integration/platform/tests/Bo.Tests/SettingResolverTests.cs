using System.Text.Json.Nodes;
using Bo.Core.Config;

namespace Bo.Tests;

internal static class NodeExtensions
{
    public static decimal Dec(this JsonNode n) => JsonNumbers.Decimal(n);
}

public class SettingResolverTests
{
    private const long Acme = 1, Betgeo = 2;
    private static readonly ScopeContext Ctx = new(Acme, BrandId: 10, SportId: 1, CategoryId: 5, TournamentId: 7, EventId: 100, MarketId: 1000, MarketTypeId: 18);
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private long _id;

    private SettingRow Row(string key, ScopeType scope, long? scopeId, JsonNode value, long? op = Acme, int? mt = null) =>
        new(++_id, scope == ScopeType.Platform ? null : op, scope, scopeId, mt, key, value, 1, T0);

    private static SettingDef Def(string key) => SettingCatalog.ByKey[key];

    [Fact]
    public void No_rows_gives_the_catalog_default()
    {
        var e = SettingResolver.Resolve(Def("margin.pct"), [], Ctx);

        Assert.True(e.IsDefault);
        Assert.Equal(0.06m, e.Value!.GetValue<decimal>());
    }

    [Fact]
    public void Deeper_level_wins_and_trace_lists_every_candidate()
    {
        var rows = new[]
        {
            Row("margin.pct", ScopeType.Operator, null, 0.07m),
            Row("margin.pct", ScopeType.Sport, 1, 0.05m),
            Row("margin.pct", ScopeType.Tournament, 7, 0.04m),
        };

        var e = SettingResolver.Resolve(Def("margin.pct"), rows, Ctx);

        Assert.Equal(0.04m, e.Value!.GetValue<decimal>());
        Assert.Equal(ScopeType.Tournament, e.Winner!.ScopeType);
        Assert.Equal([ScopeType.Tournament, ScopeType.Sport, ScopeType.Operator], e.Trace.Select(t => t.Row.ScopeType));
        Assert.Single(e.Trace, t => t.Winner);
    }

    [Fact]
    public void Market_type_qualifier_wins_at_equal_depth_but_not_over_a_deeper_level()
    {
        var sportForTotals = Row("margin.pct", ScopeType.Sport, 1, 0.03m, mt: 18);
        var sport = Row("margin.pct", ScopeType.Sport, 1, 0.05m);
        Assert.Equal(0.03m, SettingResolver.Resolve(Def("margin.pct"), [sport, sportForTotals], Ctx).Value!.GetValue<decimal>());

        // docs/09 §2.2: an event-level margin applies to all its markets, even over a qualified sport row.
        var evt = Row("margin.pct", ScopeType.Event, 100, 0.08m);
        Assert.Equal(0.08m, SettingResolver.Resolve(Def("margin.pct"), [sport, sportForTotals, evt], Ctx).Value!.GetValue<decimal>());
    }

    [Fact]
    public void Rows_for_other_market_types_events_or_operators_do_not_apply()
    {
        var rows = new[]
        {
            Row("margin.pct", ScopeType.Sport, 1, 0.01m, mt: 1),            // 1x2 only
            Row("margin.pct", ScopeType.Event, 999, 0.02m),                  // another event
            Row("margin.pct", ScopeType.Operator, null, 0.03m, op: Betgeo), // another operator
            Row("margin.pct", ScopeType.Brand, 11, 0.04m),                   // another brand
        };

        var e = SettingResolver.Resolve(Def("margin.pct"), rows, Ctx);

        Assert.True(e.IsDefault);
        Assert.Empty(e.Trace);
    }

    [Fact]
    public void Hidden_at_a_parent_hides_everything_below_even_if_set_visible_there()
    {
        var rows = new[]
        {
            Row("offer.visible", ScopeType.Category, 5, false),
            Row("offer.visible", ScopeType.Event, 100, true),
        };

        var e = SettingResolver.Resolve(Def("offer.visible"), rows, Ctx);

        Assert.False(e.Value!.GetValue<bool>());
        Assert.Equal(ScopeType.Category, e.Winner!.ScopeType);
    }

    [Fact]
    public void Platform_row_applies_to_every_operator_unless_overridden()
    {
        var platform = Row("limit.max_payout", ScopeType.Platform, null, new JsonObject { ["GEL"] = 50000 });
        Assert.Equal(50000m, SettingResolver.Resolve(Def("limit.max_payout"), [platform], Ctx).Value!["GEL"]!.Dec());

        var own = Row("limit.max_payout", ScopeType.Operator, null, new JsonObject { ["GEL"] = 20000 });
        Assert.Equal(20000m, SettingResolver.Resolve(Def("limit.max_payout"), [platform, own], Ctx).Value!["GEL"]!.Dec());
    }

    [Fact]
    public void Min_path_takes_the_strictest_amount_per_currency()
    {
        var def = Def("limit.max_stake") with { Combine = Combine.MinPath };
        var rows = new[]
        {
            Row("limit.max_stake", ScopeType.Operator, null, new JsonObject { ["GEL"] = 1000, ["USD"] = 300 }),
            Row("limit.max_stake", ScopeType.Tournament, 7, new JsonObject { ["GEL"] = 2000, ["USD"] = 100 }),
        };

        var e = SettingResolver.Resolve(def, rows, Ctx);

        Assert.Equal(1000m, e.Value!["GEL"]!.Dec());
        Assert.Equal(100m, e.Value!["USD"]!.Dec());
    }

    [Theory]
    [InlineData("margin.pct", "sport", null, "0.05", null)]
    [InlineData("margin.pct", "sport", null, "0.5", "Must be at most 0.30")]
    [InlineData("margin.pct", "platform", null, "0.05", "cannot be set at platform level")]
    [InlineData("offer.visible", "sport", 18, "true", "does not take a market type qualifier")]
    [InlineData("margin.mode", "event", 18, "\"target\"", null)]
    [InlineData("margin.mode", "event", null, "\"magic\"", "Expected one of")]
    [InlineData("limit.max_stake", "operator", null, "{\"GEL\": 100}", null)]
    [InlineData("limit.max_stake", "operator", null, "{\"lari\": 100}", "is not a currency code")]
    [InlineData("rg.min_age", "operator", null, "18", "managed by the platform")]
    public void Validator_checks_scope_qualifier_type_range_and_ownership(string key, string scope, int? mt, string json, string? error)
    {
        var result = SettingValidator.Validate(Def(key), ScopeTypes.Parse(scope), mt, JsonNode.Parse(json), platformUser: false, isDelete: false);

        if (error is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(error, result);
        }
    }

    [Fact]
    public void Catalog_is_consistent()
    {
        foreach (var d in SettingCatalog.All)
        {
            Assert.NotEmpty(d.AllowedScopes);
            if (d.Default is not null)
            {
                Assert.Null(SettingValidator.ValidateValue(d, d.Default));
            }
        }
        Assert.Equal(SettingCatalog.All.Count, SettingCatalog.All.Select(d => d.Key).Distinct().Count());
        // Decisions of 2026-10-04 (docs/09 §6.7).
        Assert.Equal(30, SettingCatalog.ByKey["referral.live_timeout_seconds"].Default!.GetValue<int>());
        Assert.Equal(180, SettingCatalog.ByKey["referral.prematch_timeout_seconds"].Default!.GetValue<int>());
        Assert.Equal(30, SettingCatalog.ByKey["referral.counter_offer_timeout_seconds"].Default!.GetValue<int>());
    }
}
