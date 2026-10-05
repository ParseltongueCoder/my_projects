using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bo.Api.Infrastructure;
using Bo.Api.Modules.Odds;
using Dapper;
using Platform.Tests;

namespace Bo.Tests;

public sealed class OddsApiTests(BoApiFixture f) : IClassFixture<BoApiFixture>
{
    private static async Task<JsonElement> Json(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode} {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string> Code(HttpResponseMessage r, HttpStatusCode expected) =>
        (await Json(r, expected)).GetProperty("code").GetString()!;

    private static async Task<JsonElement> MarketAsync(HttpClient c, long eventId, long marketId, string lang = "en")
    {
        var view = await Json(await c.GetAsync($"/api/bo/odds/events/{eventId}/markets?lang={lang}"));
        return view.GetProperty("markets").EnumerateArray().Single(m => m.GetProperty("id").GetInt64() == marketId);
    }

    private static Dictionary<string, decimal?> Odds(JsonElement market) =>
        market.GetProperty("outcomes").EnumerateArray().ToDictionary(o => o.GetProperty("code").GetString()!,
            o => o.GetProperty("odds").ValueKind == JsonValueKind.Null ? (decimal?)null : o.GetProperty("odds").GetDecimal());

    private static string Status(JsonElement market) => market.GetProperty("status").GetString()!;

    private static List<string> Reasons(JsonElement market) => market.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).ToList();

    [DbFact]
    public async Task Operator_margin_reprices_the_market_for_that_operator_only_after_four_eyes()
    {
        var set = await Json(await f.AcmeHead.PostAsJsonAsync("/api/bo/cfg/change-sets", new
        {
            title = "8% on the derby total",
            platform = false,
            changes = new object[]
            {
                new { op = "upsert", scopeType = "market", scopeId = f.TotalMarketId, marketTypeId = (int?)null, key = "margin.mode", value = "target" },
                new { op = "upsert", scopeType = "market", scopeId = f.TotalMarketId, marketTypeId = (int?)null, key = "margin.pct", value = 0.08m },
            },
        }), HttpStatusCode.Created);
        Assert.Equal("pending_approval", set.GetProperty("status").GetString());
        Assert.Equal("feed", (await MarketAsync(f.AcmeTrader, f.EventId, f.TotalMarketId)).GetProperty("mode").GetString());

        await Json(await f.AcmeAdmin.PostAsJsonAsync($"/api/bo/cfg/change-sets/{set.GetProperty("id").GetInt64()}/approve", new { comment = "ok" }));

        var acme = await MarketAsync(f.AcmeTrader, f.EventId, f.TotalMarketId);
        Assert.Equal("target", acme.GetProperty("mode").GetString());
        Assert.Equal(new Dictionary<string, decimal?> { ["12"] = 1.80m, ["13"] = 1.89m }, Odds(acme));
        Assert.Equal(0.0534m, acme.GetProperty("feedOverround").GetDecimal());
        Assert.Equal("Total", acme.GetProperty("name").GetString());
        Assert.Equal("over 2.5", acme.GetProperty("outcomes")[0].GetProperty("name").GetString());
        Assert.Equal("margin", acme.GetProperty("outcomes")[0].GetProperty("source").GetString());

        var betgeo = await MarketAsync(f.BetgeoAdmin, f.EventId, f.TotalMarketId);
        Assert.Equal(new Dictionary<string, decimal?> { ["12"] = 1.85m, ["13"] = 1.95m }, Odds(betgeo));
    }

    [DbFact]
    public async Task Odds_override_has_a_ttl_wins_over_the_feed_and_is_invisible_to_other_operators()
    {
        Assert.Equal("TTL_REQUIRED", await Code(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/overrides",
            new { marketId = f.FeedMarketId, outcomeCode = "1", kind = "absolute", value = 2.5m, reason = "no ttl" }), HttpStatusCode.BadRequest));
        Assert.Equal("TTL_TOO_LONG", await Code(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/overrides",
            new { marketId = f.FeedMarketId, outcomeCode = "1", kind = "absolute", value = 2.5m, ttlMinutes = 2000, reason = "too long" }), HttpStatusCode.BadRequest));
        Assert.Equal("UNKNOWN_OUTCOME", await Code(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/overrides",
            new { marketId = f.FeedMarketId, outcomeCode = "99", kind = "absolute", value = 2.5m, ttlMinutes = 30, reason = "x" }), HttpStatusCode.BadRequest));

        var created = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/overrides",
            new { marketId = f.FeedMarketId, outcomeCode = "1", kind = "absolute", value = 2.5m, ttlMinutes = 30, reason = "liability on the away side" }),
            HttpStatusCode.Created);
        var id = created.GetProperty("override").GetProperty("id").GetInt64();
        Assert.Contains(created.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("away from the feed price 2.10"));

        var acme = await MarketAsync(f.AcmeTrader, f.EventId, f.FeedMarketId);
        Assert.Equal(2.50m, Odds(acme)["1"]);
        Assert.Equal("override", acme.GetProperty("outcomes")[0].GetProperty("source").GetString());
        Assert.True(acme.GetProperty("outcomes")[0].GetProperty("overrideApplied").GetBoolean());
        // 1/2.50 + 1/3.40 + 1/3.60 < 1: the price would be an arbitrage, so the market is held.
        Assert.Equal("suspended", Status(acme));
        Assert.Contains("sanity:margin_floor", Reasons(acme));

        var betgeo = await MarketAsync(f.BetgeoAdmin, f.EventId, f.FeedMarketId);
        Assert.Equal(2.10m, Odds(betgeo)["1"]);
        Assert.Equal("active", Status(betgeo));
        Assert.Empty((await Json(await f.BetgeoAdmin.GetAsync("/api/bo/odds/overrides"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await f.BetgeoAdmin.DeleteAsync($"/api/bo/odds/overrides/{id}")).StatusCode);

        // A new override on the same outcome replaces the old one.
        var shift = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/overrides",
            new { marketId = f.FeedMarketId, outcomeCode = "1", kind = "shift_pct", value = -0.05m, ttlMinutes = 30, reason = "shade the favourite" }),
            HttpStatusCode.Created);
        Assert.Equal(1.99m, shift.GetProperty("market").GetProperty("outcomes")[0].GetProperty("odds").GetDecimal());  // 2.10 × 0.95 = 1.995
        var list = await Json(await f.AcmeTrader.GetAsync($"/api/bo/odds/overrides?eventId={f.EventId}"));
        Assert.Single(list.EnumerateArray());
        Assert.Equal("Dinamo Tbilisi v Torpedo Kutaisi", list[0].GetProperty("eventName").GetString());

        var shiftId = shift.GetProperty("override").GetProperty("id").GetInt64();
        await Json(await f.AcmeTrader.DeleteAsync($"/api/bo/odds/overrides/{shiftId}?reason=done"), HttpStatusCode.NoContent);
        Assert.Equal(2.10m, Odds(await MarketAsync(f.AcmeTrader, f.EventId, f.FeedMarketId))["1"]);

        var audit = await Json(await f.AcmeAdmin.GetAsync($"/api/bo/adm/audit?entityType=odds_override&entityId={shiftId}"));
        Assert.Equal(["odds.override.cleared", "odds.override.created"], audit.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("action").GetString()));
    }

    [DbFact]
    public async Task Expired_overrides_and_suspensions_are_swept_with_an_audit_row()
    {
        var created = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/overrides",
            new { marketId = f.TotalMarketId, outcomeCode = "13", kind = "absolute", value = 2.2m, ttlMinutes = 5, reason = "short" }), HttpStatusCode.Created);
        var overrideId = created.GetProperty("override").GetProperty("id").GetInt64();
        var suspension = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/trading",
            new { scopeType = "market", scopeId = f.TotalMarketId, action = "suspend", ttlMinutes = 5, reason = "news" }), HttpStatusCode.Created);

        await using (var conn = await f.Db.DataSource.OpenConnectionAsync())
        {
            await conn.ExecuteAsync("UPDATE bo.odds_override SET created_at = now() - interval '1 hour', expires_at = now() - interval '1 second' WHERE id = @overrideId", new { overrideId });
            await conn.ExecuteAsync("UPDATE bo.trading_override SET expires_at = now() - interval '1 second' WHERE id = @id", new { id = suspension.GetProperty("id").GetInt64() });
        }
        // Read time already ignores them; the sweep then clears them.
        var market = await MarketAsync(f.AcmeTrader, f.EventId, f.TotalMarketId);
        Assert.False(market.GetProperty("outcomes")[1].GetProperty("overrideApplied").GetBoolean());
        Assert.DoesNotContain("trading:suspend", Reasons(market));

        Assert.True(await TradingExpiryWorker.RunOnceAsync(f.Db.DataSource) >= 2);
        await using (var conn = await f.Db.DataSource.OpenConnectionAsync())
        {
            Assert.Equal("system:expiry", await conn.ExecuteScalarAsync<string>("SELECT cleared_by_name FROM bo.odds_override WHERE id = @overrideId", new { overrideId }));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM bo.audit_log WHERE action = 'odds.override.cleared' AND entity_id = @id AND actor_type = 'system'", new { id = overrideId.ToString() }));
            Assert.True(await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM bo.outbox WHERE topic = 'bo.changed.1.odds' AND payload->>'marketId' = @m)",
                new { m = f.TotalMarketId.ToString() }));
        }
        Assert.DoesNotContain((await Json(await f.AcmeTrader.GetAsync($"/api/bo/odds/overrides?eventId={f.EventId}"))).EnumerateArray(),
            o => o.GetProperty("marketId").GetInt64() == f.TotalMarketId);
    }

    [DbFact]
    public async Task Suspend_and_close_per_operator_and_platform_wide()
    {
        var suspend = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/trading",
            new { scopeType = "event", scopeId = f.OtherEventId, action = "suspend", reason = "team news" }), HttpStatusCode.Created);
        Assert.Equal("DUPLICATE", await Code(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/trading",
            new { scopeType = "event", scopeId = f.OtherEventId, action = "suspend", reason = "again" }), HttpStatusCode.Conflict));
        var acme = await MarketAsync(f.AcmeTrader, f.OtherEventId, f.OtherMarketId);
        Assert.Equal("suspended", Status(acme));
        Assert.Equal(["trading:suspend"], Reasons(acme));
        Assert.Equal(1.80m, Odds(acme)["1"]);  // prices stay visible, betting stops
        Assert.Equal("active", Status(await MarketAsync(f.BetgeoAdmin, f.OtherEventId, f.OtherMarketId)));

        var close = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/trading",
            new { scopeType = "market", scopeId = f.OtherMarketId, action = "close", reason = "not offered" }), HttpStatusCode.Created);
        acme = await MarketAsync(f.AcmeTrader, f.OtherEventId, f.OtherMarketId);
        Assert.Equal("hidden", Status(acme));
        Assert.Equal(["trading:close", "trading:suspend"], Reasons(acme));
        Assert.Equal(2, (await Json(await f.AcmeTrader.GetAsync($"/api/bo/odds/trading?eventId={f.OtherEventId}"))).GetArrayLength());

        // Platform staff suspend for every operator (incident response); operators see it but cannot lift it.
        Assert.Equal("PLATFORM_ONLY", await Code(await f.AcmeAdmin.PostAsJsonAsync("/api/bo/odds/trading",
            new { scopeType = "event", scopeId = f.OtherEventId, action = "suspend", reason = "x", platform = true }), HttpStatusCode.Forbidden));
        var platform = await Json(await f.Platform().PostAsJsonAsync("/api/bo/odds/trading",
            new { scopeType = "event", scopeId = f.OtherEventId, action = "suspend", reason = "wrong feed prices", platform = true }), HttpStatusCode.Created);
        var betgeo = await MarketAsync(f.BetgeoAdmin, f.OtherEventId, f.OtherMarketId);
        Assert.Equal("suspended", Status(betgeo));
        var platformId = platform.GetProperty("id").GetInt64();
        Assert.Equal("PLATFORM_ONLY", await Code(await f.BetgeoAdmin.DeleteAsync($"/api/bo/odds/trading/{platformId}"), HttpStatusCode.Forbidden));
        await Json(await f.Platform().DeleteAsync($"/api/bo/odds/trading/{platformId}?reason=feed fixed"), HttpStatusCode.NoContent);
        Assert.Equal("active", Status(await MarketAsync(f.BetgeoAdmin, f.OtherEventId, f.OtherMarketId)));

        // BetGeo cannot lift Acme's suspension; Acme can.
        Assert.Equal(HttpStatusCode.NotFound, (await f.BetgeoAdmin.DeleteAsync($"/api/bo/odds/trading/{suspend.GetProperty("id").GetInt64()}")).StatusCode);
        await Json(await f.AcmeTrader.DeleteAsync($"/api/bo/odds/trading/{suspend.GetProperty("id").GetInt64()}"), HttpStatusCode.NoContent);
        await Json(await f.AcmeTrader.DeleteAsync($"/api/bo/odds/trading/{close.GetProperty("id").GetInt64()}"), HttpStatusCode.NoContent);
        Assert.Equal("active", Status(await MarketAsync(f.AcmeTrader, f.OtherEventId, f.OtherMarketId)));
    }

    [DbFact]
    public async Task Manual_markets_are_priced_by_the_trader_and_belong_to_one_operator()
    {
        Assert.Equal("BAD_SPECIFIERS", await Code(await f.AcmeTrader.PostAsJsonAsync($"/api/bo/odds/events/{f.EventId}/manual-markets",
            new { marketTypeId = f.TotalMarketTypeId, outcomes = new[] { new { code = "12", odds = 1.9m } } }), HttpStatusCode.BadRequest));
        Assert.Equal("UNKNOWN_OUTCOME", await Code(await f.AcmeTrader.PostAsJsonAsync($"/api/bo/odds/events/{f.EventId}/manual-markets",
            new { marketTypeId = f.TotalMarketTypeId, specifiers = "total=3.5", outcomes = new[] { new { code = "1", odds = 1.9m } } }), HttpStatusCode.BadRequest));

        var created = await Json(await f.AcmeTrader.PostAsJsonAsync($"/api/bo/odds/events/{f.EventId}/manual-markets", new
        {
            marketTypeId = f.TotalMarketTypeId,
            specifiers = "total=3.5",
            outcomes = new[] { new { code = "12", odds = 1.833m }, new { code = "13", odds = 1.95m } },
            reason = "derby special",
        }), HttpStatusCode.Created);
        var marketId = created.GetProperty("id").GetInt64();
        Assert.True(created.GetProperty("isManual").GetBoolean());
        Assert.Equal("manual", created.GetProperty("mode").GetString());
        Assert.Equal("Total", created.GetProperty("name").GetString());
        Assert.Equal("over 3.5", created.GetProperty("outcomes")[0].GetProperty("name").GetString());
        Assert.Equal(new Dictionary<string, decimal?> { ["12"] = 1.83m, ["13"] = 1.95m }, Odds(created));
        Assert.Equal("active", Status(created));

        // Not visible, not editable for BetGeo, who can add the same special for themselves.
        var betgeoView = await Json(await f.BetgeoAdmin.GetAsync($"/api/bo/odds/events/{f.EventId}/markets"));
        Assert.DoesNotContain(betgeoView.GetProperty("markets").EnumerateArray(), m => m.GetProperty("id").GetInt64() == marketId);
        Assert.Equal(HttpStatusCode.NotFound, (await f.BetgeoAdmin.PatchAsJsonAsync($"/api/bo/odds/manual-markets/{marketId}", new { status = "suspended" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.BetgeoAdmin.PostAsJsonAsync("/api/bo/odds/overrides",
            new { marketId, outcomeCode = "12", kind = "absolute", value = 2m, ttlMinutes = 5, reason = "x" })).StatusCode);
        await Json(await f.BetgeoAdmin.PostAsJsonAsync($"/api/bo/odds/events/{f.EventId}/manual-markets", new
        {
            marketTypeId = f.TotalMarketTypeId, specifiers = "total=3.5", outcomes = new[] { new { code = "12", odds = 1.8m }, new { code = "13", odds = 2.0m } },
        }), HttpStatusCode.Created);
        Assert.Equal("DUPLICATE", await Code(await f.AcmeTrader.PostAsJsonAsync($"/api/bo/odds/events/{f.EventId}/manual-markets", new
        {
            marketTypeId = f.TotalMarketTypeId, specifiers = "total=3.5", outcomes = new[] { new { code = "12", odds = 1.8m } },
        }), HttpStatusCode.Conflict));

        var patched = await Json(await f.AcmeTrader.PatchAsJsonAsync($"/api/bo/odds/manual-markets/{marketId}", new
        {
            outcomes = new[] { new { code = "12", odds = 2.05m } }, status = "suspended", reason = "red card",
        }));
        Assert.Equal(2.04m, Odds(patched)["12"]);  // ladder step 0.02 above 2.00
        Assert.Equal("suspended", Status(patched));
        Assert.Equal(["feed:suspended"], Reasons(patched));

        // Overrides are for feed markets; the open market count of BetGeo does not include Acme's special.
        Assert.Equal("MANUAL_MARKET", await Code(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/overrides",
            new { marketId, outcomeCode = "12", kind = "absolute", value = 2m, ttlMinutes = 5, reason = "x" }), HttpStatusCode.BadRequest));
        var acmeEvent = await Json(await f.AcmeAdmin.GetAsync($"/api/bo/cat/events/{f.EventId}"));
        var betgeoEvent = await Json(await f.BetgeoAdmin.GetAsync($"/api/bo/cat/events/{f.EventId}"));
        Assert.Equal(betgeoEvent.GetProperty("event").GetProperty("openMarkets").GetInt32(), acmeEvent.GetProperty("event").GetProperty("openMarkets").GetInt32());
    }

    [DbFact]
    public async Task The_database_keeps_bo_away_from_feed_rows_and_other_operators_manual_rows()
    {
        var created = await Json(await f.AcmeTrader.PostAsJsonAsync($"/api/bo/odds/events/{f.OtherEventId}/manual-markets", new
        {
            marketTypeId = f.MarketTypeId, outcomes = new[] { new { code = "1", odds = 2m }, new { code = "2", odds = 3m }, new { code = "3", odds = 4m } },
        }), HttpStatusCode.Created);
        var manualId = created.GetProperty("id").GetInt64();
        var ov = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/overrides",
            new { marketId = f.OtherMarketId, outcomeCode = "2", kind = "absolute", value = 3.5m, ttlMinutes = 10, reason = "rls test" }), HttpStatusCode.Created);
        var suspension = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/trading",
            new { scopeType = "market", scopeId = f.OtherMarketId, action = "suspend", reason = "rls test" }), HttpStatusCode.Created);

        var db = new BoDb(f.Db.DataSource);
        var acme = TenantContext.System(new OperatorRef(1, "acmebet", "AcmeBet"));
        var betgeo = TenantContext.System(new OperatorRef(2, "betgeo", "BetGeo"));
        var seen = await db.TenantAsync(betgeo, async (conn, tx) => new
        {
            overrides = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM bo.odds_override", transaction: tx),
            trading = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM bo.trading_override WHERE operator_id IS NOT NULL", transaction: tx),
            manual = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM bo.manual_entity WHERE entity_id = @manualId", new { manualId }, tx),
            // Another operator's manual market and any feed market: no rows change, nothing is inserted.
            updatedManual = await conn.ExecuteAsync("UPDATE sb.market SET status = 'deactivated' WHERE id = @manualId", new { manualId }, tx),
            updatedOutcome = await conn.ExecuteAsync("UPDATE sb.outcome SET odds = 9 WHERE market_id = @manualId", new { manualId }, tx),
            updatedOverride = await conn.ExecuteAsync("UPDATE bo.odds_override SET value = 9", transaction: tx),
        });
        Assert.Equal(0, seen.overrides);
        Assert.Equal(0, seen.trading);
        Assert.Equal(0, seen.manual);
        Assert.Equal(0, seen.updatedManual);
        Assert.Equal(0, seen.updatedOutcome);
        Assert.Equal(0, seen.updatedOverride);

        Assert.Equal(0, await db.TenantAsync(acme, (conn, tx) =>
            conn.ExecuteAsync("UPDATE sb.market SET status = 'deactivated' WHERE id = @id", new { id = f.FeedMarketId }, tx)));
        Assert.Equal(0, await db.TenantAsync(acme, (conn, tx) =>
            conn.ExecuteAsync("UPDATE sb.outcome SET odds = 9 WHERE market_id = @id", new { id = f.FeedMarketId }, tx)));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.TenantAsync(acme, (conn, tx) => conn.ExecuteAsync("""
            INSERT INTO sb.market (event_id, market_description_id, specifiers, status, feed_status, source_producer_id, last_feed_ts)
            VALUES (@e, @t, 'x=1', 'active', 'active', 3, now())
            """, new { e = f.EventId, t = f.MarketTypeId }, tx)));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.TenantAsync(betgeo, (conn, tx) => conn.ExecuteAsync(
            "INSERT INTO bo.odds_override (operator_id, market_id, outcome_code, kind, value, expires_at, reason) VALUES (1, @m, '1', 'absolute', 2, now() + interval '1 hour', 'x')",
            new { m = f.FeedMarketId }, tx)));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.TenantAsync(betgeo, (conn, tx) => conn.ExecuteAsync(
            "INSERT INTO sb.market_description (code, name_template_i18n) VALUES ('evil', '{}')", transaction: tx)));

        // The owner can still change its manual market at the database level.
        Assert.Equal(1, await db.TenantAsync(acme, (conn, tx) =>
            conn.ExecuteAsync("UPDATE sb.market SET status = 'suspended' WHERE id = @manualId", new { manualId }, tx)));

        // Leave the shared event as it was for the other tests of this class.
        await Json(await f.AcmeTrader.DeleteAsync($"/api/bo/odds/overrides/{ov.GetProperty("override").GetProperty("id").GetInt64()}"), HttpStatusCode.NoContent);
        await Json(await f.AcmeTrader.DeleteAsync($"/api/bo/odds/trading/{suspension.GetProperty("id").GetInt64()}"), HttpStatusCode.NoContent);
        await Json(await f.AcmeTrader.PatchAsJsonAsync($"/api/bo/odds/manual-markets/{manualId}", new { status = "deactivated" }));
    }

    [DbFact]
    public async Task Market_type_matrix_disables_a_type_per_sport_through_cfg()
    {
        var matrix = await Json(await f.AcmeTrader.GetAsync("/api/bo/odds/market-types"));
        var total = matrix.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == f.TotalMarketTypeId);
        Assert.True(total.GetProperty("sportCells").GetProperty(f.SportId.ToString()).GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(matrix.GetProperty("items").EnumerateArray(), i => i.GetProperty("code").GetString()!.StartsWith("manual:"));

        Assert.Equal("INHERITED", await Code(await f.AcmeTrader.PutAsJsonAsync($"/api/bo/odds/market-types/{f.TotalMarketTypeId}",
            new { sportId = f.SportId, enabled = true, reason = "nothing to enable" }), HttpStatusCode.BadRequest));
        var set = await Json(await f.AcmeTrader.PutAsJsonAsync($"/api/bo/odds/market-types/{f.TotalMarketTypeId}",
            new { sportId = f.SportId, enabled = false, reason = "totals not offered on soccer" }));
        Assert.Equal("applied", set.GetProperty("status").GetString());

        var hidden = await MarketAsync(f.AcmeTrader, f.EventId, f.TotalMarketId);
        Assert.Equal("hidden", Status(hidden));
        Assert.Contains("cfg:market.enabled", Reasons(hidden));
        Assert.Equal("active", Status(await MarketAsync(f.BetgeoAdmin, f.EventId, f.TotalMarketId)));
        var cell = (await Json(await f.AcmeTrader.GetAsync("/api/bo/odds/market-types"))).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetInt32() == f.TotalMarketTypeId).GetProperty("sportCells").GetProperty(f.SportId.ToString());
        Assert.False(cell.GetProperty("enabled").GetBoolean());
        Assert.False(cell.GetProperty("setHere").GetBoolean());

        await Json(await f.AcmeTrader.PutAsJsonAsync($"/api/bo/odds/market-types/{f.TotalMarketTypeId}",
            new { sportId = f.SportId, enabled = true, reason = "back" }));
        Assert.NotEqual("hidden", Status(await MarketAsync(f.AcmeTrader, f.EventId, f.TotalMarketId)));
    }

    [DbFact]
    public async Task Simulator_prices_sample_or_live_markets_with_what_if_settings()
    {
        var sample = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/simulate", new
        {
            outcomes = new[] { new { code = "1", odds = 2.10m }, new { code = "X", odds = 3.40m }, new { code = "2", odds = 3.60m } },
            settings = new { mode = "target", pct = 0.07m },
        }));
        var odds = sample.GetProperty("priced").GetProperty("outcomes").EnumerateArray().Select(o => o.GetProperty("odds").GetDecimal()).ToList();
        Assert.Equal([2.06m, 3.30m, 3.50m], odds);

        var live = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/simulate", new
        {
            marketId = f.OtherMarketId, settings = new { mode = "delta", deltaPct = 0.05m },
        }));
        Assert.Equal(1.76m, live.GetProperty("priced").GetProperty("outcomes")[0].GetProperty("odds").GetDecimal());  // 1 + 0.80 × 0.95
        Assert.Equal(f.OtherMarketId, live.GetProperty("current").GetProperty("id").GetInt64());
        Assert.Equal("BAD_MARGIN", await Code(await f.AcmeTrader.PostAsJsonAsync("/api/bo/odds/simulate",
            new { marketId = f.OtherMarketId, settings = new { pct = 0.9m } }), HttpStatusCode.BadRequest));
    }
}
