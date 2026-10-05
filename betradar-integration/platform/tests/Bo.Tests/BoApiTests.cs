using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bo.Api.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Platform.Tests;

namespace Bo.Tests;

/// <summary>Bo.Api over a fresh database with the dev seed (two operators) and a small sb catalogue.</summary>
public sealed class BoApiFixture : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private WebApplicationFactory<Program>? _factory;

    public TestDatabase Db => _db;
    public long SportId { get; private set; }
    public long TournamentId { get; private set; }
    public long OtherTournamentId { get; private set; }
    public int MarketTypeId { get; private set; }
    public int TotalMarketTypeId { get; private set; }
    public long CategoryId { get; private set; }
    public long EventId { get; private set; }
    public long HomeId { get; private set; }
    public long AwayId { get; private set; }
    public long OtherEventId { get; private set; }
    /// <summary>Feed 1x2 on <see cref="EventId"/>: 2.10 / 3.40 / 3.60.</summary>
    public long FeedMarketId { get; private set; }
    /// <summary>Feed total=2.5 on <see cref="EventId"/>: over 1.85 / under 1.95.</summary>
    public long TotalMarketId { get; private set; }
    /// <summary>Feed 1x2 on <see cref="OtherEventId"/>: 1.80 / 3.60 / 4.50.</summary>
    public long OtherMarketId { get; private set; }

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (TestDatabase.ServerConnectionString is null)
        {
            return;
        }
        var connectionString = _db.ConnectionString;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Platform", connectionString);
            b.UseSetting("Auth:Enabled", "false");
            b.UseSetting("Bo:DevSeed", "true");
        });
        _ = _factory.Server; // start: migrations, catalog sync, dev seed

        await using var conn = await _db.DataSource.OpenConnectionAsync();
        SportId = await conn.ExecuteScalarAsync<long>("INSERT INTO sb.sport (code, name_i18n) VALUES ('soccer', '{\"en\":\"Soccer\"}') RETURNING id");
        var category = await conn.ExecuteScalarAsync<long>("INSERT INTO sb.category (sport_id, name_i18n, country_code) VALUES (@SportId, '{\"en\":\"Georgia\"}', 'GEO') RETURNING id", new { SportId });
        TournamentId = await conn.ExecuteScalarAsync<long>("INSERT INTO sb.tournament (sport_id, category_id, name_i18n) VALUES (@SportId, @category, '{\"en\":\"Erovnuli Liga\"}') RETURNING id", new { SportId, category });
        OtherTournamentId = await conn.ExecuteScalarAsync<long>("INSERT INTO sb.tournament (sport_id, category_id, name_i18n) VALUES (@SportId, @category, '{\"en\":\"Liga 2\"}') RETURNING id", new { SportId, category });
        MarketTypeId = await conn.ExecuteScalarAsync<int>("INSERT INTO sb.market_description (code, name_template_i18n) VALUES ('1x2', '{\"en\":\"1x2\"}') RETURNING id");
        CategoryId = category;
        await conn.ExecuteAsync("""
            INSERT INTO sb.market_description_outcome (market_description_id, code, name_template_i18n, ordinal) VALUES
              (@MarketTypeId, '1', '{"en":"{$competitor1}"}', 1), (@MarketTypeId, '2', '{"en":"draw"}', 2), (@MarketTypeId, '3', '{"en":"{$competitor2}"}', 3)
            """, new { MarketTypeId });
        TotalMarketTypeId = await conn.ExecuteScalarAsync<int>("INSERT INTO sb.market_description (code, name_template_i18n) VALUES ('total', '{\"en\":\"Total\"}') RETURNING id");
        await conn.ExecuteAsync("""
            INSERT INTO sb.market_description_outcome (market_description_id, code, name_template_i18n, ordinal) VALUES
              (@TotalMarketTypeId, '12', '{"en":"over {total}"}', 1), (@TotalMarketTypeId, '13', '{"en":"under {total}"}', 2)
            """, new { TotalMarketTypeId });
        HomeId = await conn.ExecuteScalarAsync<long>("INSERT INTO sb.competitor (sport_id, name_i18n, country_code) VALUES (@SportId, '{\"en\":\"Dinamo Tbilisi\"}', 'GEO') RETURNING id", new { SportId });
        AwayId = await conn.ExecuteScalarAsync<long>("INSERT INTO sb.competitor (sport_id, name_i18n, country_code) VALUES (@SportId, '{\"en\":\"Torpedo Kutaisi\"}', 'GEO') RETURNING id", new { SportId });
        EventId = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO sb.event (event_type, sport_id, tournament_id, scheduled_at) VALUES ('match', @SportId, @TournamentId, now() + interval '1 day') RETURNING id
            """, new { SportId, TournamentId });
        await conn.ExecuteAsync("INSERT INTO sb.event_competitor (event_id, position, competitor_id, qualifier) VALUES (@EventId, 1, @HomeId, 'home'), (@EventId, 2, @AwayId, 'away')",
            new { EventId, HomeId, AwayId });
        OtherEventId = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO sb.event (event_type, sport_id, tournament_id, scheduled_at) VALUES ('match', @SportId, @OtherTournamentId, now() + interval '2 days') RETURNING id
            """, new { SportId, OtherTournamentId });
        await conn.ExecuteAsync("INSERT INTO sb.event_competitor (event_id, position, competitor_id, qualifier) VALUES (@OtherEventId, 1, @AwayId, 'home'), (@OtherEventId, 2, @HomeId, 'away')",
            new { OtherEventId, HomeId, AwayId });
        await conn.ExecuteAsync("INSERT INTO sb.market_specifier_def (market_description_id, name, type) VALUES (@TotalMarketTypeId, 'total', 'decimal')", new { TotalMarketTypeId });
        FeedMarketId = await FeedMarketAsync(conn, EventId, MarketTypeId, "", ("1", 2.10m), ("2", 3.40m), ("3", 3.60m));
        TotalMarketId = await FeedMarketAsync(conn, EventId, TotalMarketTypeId, "total=2.5", ("12", 1.85m), ("13", 1.95m));
        OtherMarketId = await FeedMarketAsync(conn, OtherEventId, MarketTypeId, "", ("1", 1.80m), ("2", 3.60m), ("3", 4.50m));
    }

    private static async Task<long> FeedMarketAsync(Npgsql.NpgsqlConnection conn, long eventId, int typeId, string specifiers, params (string Code, decimal Odds)[] outcomes)
    {
        var json = specifiers.Length == 0 ? "{}" : JsonSerializer.Serialize(specifiers.Split('|').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]));
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO sb.market (event_id, market_description_id, specifiers, specifiers_json, status, feed_status, source_producer_id, last_feed_ts)
            VALUES (@eventId, @typeId, @specifiers, @json::jsonb, 'active', 'active', 3, now()) RETURNING id
            """, new { eventId, typeId, specifiers, json });
        foreach (var (code, odds) in outcomes)
        {
            await conn.ExecuteAsync("""
                INSERT INTO sb.outcome (market_id, code, description_outcome_id, odds, is_active, odds_updated_at)
                VALUES (@id, @code, (SELECT id FROM sb.market_description_outcome WHERE market_description_id = @typeId AND code = @code), @odds, true, now())
                """, new { id, code, typeId, odds });
        }
        return id;
    }

    public HttpClient Platform(long? operatorId = null, Guid? user = null)
    {
        var c = _factory!.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", (user ?? DevSeed.PlatformAdmin).ToString());
        c.DefaultRequestHeaders.Add("X-Dev-Platform", "1");
        if (operatorId is { } id)
        {
            c.DefaultRequestHeaders.Add("X-Operator-Id", id.ToString());
        }
        return c;
    }

    public HttpClient Operator(Guid user, string org)
    {
        var c = _factory!.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", user.ToString());
        c.DefaultRequestHeaders.Add("X-Dev-Org", org);
        return c;
    }

    public HttpClient AcmeAdmin => Operator(DevSeed.AcmeAdmin, "acmebet");
    public HttpClient AcmeTrader => Operator(DevSeed.AcmeTrader, "acmebet");
    public HttpClient AcmeHead => Operator(DevSeed.AcmeHeadTrader, "acmebet");
    public HttpClient BetgeoAdmin => Operator(DevSeed.BetgeoAdmin, "betgeo");

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        await _db.DisposeAsync();
    }
}

public sealed class BoApiTests(BoApiFixture f) : IClassFixture<BoApiFixture>
{
    private static async Task<JsonElement> Json(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode} {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string> ProblemCode(HttpResponseMessage r, HttpStatusCode expected)
    {
        var json = await Json(r, expected);
        return json.GetProperty("code").GetString()!;
    }

    private object ChangeSet(string title, string key, object value, string scope = "tournament", long? scopeId = null, int? marketTypeId = null) => new
    {
        title,
        platform = false,
        changes = new[] { new { op = "upsert", scopeType = scope, scopeId = scope is "operator" or "platform" ? (long?)null : scopeId ?? f.TournamentId, marketTypeId, key, value } },
    };

    [DbFact]
    public async Task Me_reflects_operator_membership_and_rejects_a_foreign_organization()
    {
        var me = await Json(await f.AcmeTrader.GetAsync("/api/bo/me"));
        Assert.Equal("acmebet", me.GetProperty("operator").GetProperty("code").GetString());
        Assert.False(me.GetProperty("isPlatform").GetBoolean());
        Assert.Contains("cfg.edit", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.DoesNotContain("cfg.approve", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        // A token for another organization does not unlock the account (and never switches tenant).
        Assert.Equal("TENANT_MISMATCH", await ProblemCode(await f.Operator(DevSeed.AcmeTrader, "betgeo").GetAsync("/api/bo/me"), HttpStatusCode.Forbidden));
        // Unknown user.
        Assert.Equal("USER_NOT_PROVISIONED", await ProblemCode(await f.Operator(Guid.NewGuid(), "acmebet").GetAsync("/api/bo/me"), HttpStatusCode.Forbidden));
    }

    [DbFact]
    public async Task Operators_never_see_each_others_data()
    {
        var created = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/cfg/change-sets",
            ChangeSet("Erovnuli hidden for Acme", "offer.visible", false)), HttpStatusCode.Created);
        var acmeSetId = created.GetProperty("id").GetInt64();

        var acmeBrands = await Json(await f.AcmeAdmin.GetAsync("/api/bo/brands"));
        var acmeBrandId = acmeBrands[0].GetProperty("id").GetInt64();

        var betgeo = f.BetgeoAdmin;
        // Lists contain only BetGeo rows.
        Assert.All((await Json(await betgeo.GetAsync("/api/bo/cfg/change-sets"))).EnumerateArray(),
            s => Assert.Equal(2, s.GetProperty("operatorId").GetInt64()));
        Assert.All((await Json(await betgeo.GetAsync("/api/bo/brands"))).EnumerateArray(),
            b => Assert.Equal("betgeo", b.GetProperty("code").GetString()));
        Assert.All((await Json(await betgeo.GetAsync("/api/bo/adm/users"))).EnumerateArray(),
            u => Assert.Equal(2, u.GetProperty("operatorId").GetInt64()));
        Assert.All((await Json(await betgeo.GetAsync("/api/bo/adm/audit"))).GetProperty("items").EnumerateArray(),
            a => Assert.Equal(2, a.GetProperty("operatorId").GetInt64()));

        // By id: 404, not 403 (no existence leak).
        Assert.Equal(HttpStatusCode.NotFound, (await betgeo.GetAsync($"/api/bo/cfg/change-sets/{acmeSetId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await betgeo.GetAsync($"/api/bo/cfg/effective?brandId={acmeBrandId}")).StatusCode);
        Assert.Equal("UNKNOWN_SCOPE", await ProblemCode(await betgeo.PostAsJsonAsync("/api/bo/cfg/change-sets",
            ChangeSet("Use Acme brand", "offer.visible", false, "brand", acmeBrandId)), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NotFound, (await betgeo.PostAsJsonAsync($"/api/bo/cfg/change-sets/{acmeSetId}/approve", new { })).StatusCode);

        // Acme's setting does not affect BetGeo.
        var effective = await Json(await betgeo.GetAsync($"/api/bo/cfg/effective?tournamentId={f.TournamentId}&keys=offer.visible"));
        Assert.True(effective[0].GetProperty("value").GetBoolean());
        Assert.True(effective[0].GetProperty("isDefault").GetBoolean());
    }

    [DbFact]
    public async Task Row_level_security_isolates_tenants_even_without_application_filters()
    {
        var db = new BoDb(f.Db.DataSource);
        var acme = TenantContext.System(new OperatorRef(1, "acmebet", "AcmeBet"));
        // System context for operator 1 has the platform flag on, which still must not reveal other tenants.
        var counts = await db.TenantAsync(acme, async (conn, tx) => new
        {
            brands = await conn.QueryAsync<long>("SELECT DISTINCT operator_id FROM bo.brand", transaction: tx),
            users = await conn.QueryAsync<long?>("SELECT DISTINCT operator_id FROM bo.admin_user", transaction: tx),
            modules = await conn.QueryAsync<long>("SELECT DISTINCT operator_id FROM bo.operator_module", transaction: tx),
        });
        Assert.Equal([1L], counts.brands);
        Assert.DoesNotContain(2L, counts.users);
        Assert.Equal([1L], counts.modules);

        // Writing another tenant's row is refused by the database itself.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.TenantAsync(acme, (conn, tx) =>
            conn.ExecuteAsync("INSERT INTO bo.brand (operator_id, code, name) VALUES (2, 'evil', 'x')", transaction: tx)));

        // No tenant in context: tenant tables are empty (fail closed).
        var none = await db.TenantAsync(new TenantContext(), (conn, tx) => conn.ExecuteScalarAsync<long>("SELECT count(*) FROM bo.brand", transaction: tx));
        Assert.Equal(0, none);
    }

    [DbFact]
    public async Task Approval_keys_need_a_second_user_with_cfg_approve()
    {
        var created = await Json(await f.AcmeTrader.PostAsJsonAsync("/api/bo/cfg/change-sets",
            ChangeSet("Lower margin on Erovnuli 1x2", "margin.pct", 0.04m, marketTypeId: f.MarketTypeId)), HttpStatusCode.Created);
        Assert.Equal("pending_approval", created.GetProperty("status").GetString());
        var id = created.GetProperty("id").GetInt64();

        // Pending: not effective yet.
        var before = await Json(await f.AcmeTrader.GetAsync($"/api/bo/cfg/effective?tournamentId={f.TournamentId}&marketTypeId={f.MarketTypeId}&keys=margin.pct"));
        Assert.True(before[0].GetProperty("isDefault").GetBoolean());

        Assert.Equal("PERMISSION_DENIED", await ProblemCode(await f.AcmeTrader.PostAsJsonAsync($"/api/bo/cfg/change-sets/{id}/approve", new { }), HttpStatusCode.Forbidden));

        // Head trader may approve others' sets, never their own.
        var own = await Json(await f.AcmeHead.PostAsJsonAsync("/api/bo/cfg/change-sets",
            ChangeSet("Own margin", "margin.pct", 0.05m, scope: "sport", scopeId: f.SportId)), HttpStatusCode.Created);
        Assert.Equal("FOUR_EYES", await ProblemCode(await f.AcmeHead.PostAsJsonAsync($"/api/bo/cfg/change-sets/{own.GetProperty("id").GetInt64()}/approve", new { }), HttpStatusCode.Forbidden));

        var approved = await Json(await f.AcmeHead.PostAsJsonAsync($"/api/bo/cfg/change-sets/{id}/approve", new { comment = "agreed with risk" }));
        Assert.Equal("applied", approved.GetProperty("status").GetString());
        Assert.Equal("Acme Head Trader", approved.GetProperty("decidedByName").GetString());

        var after = await Json(await f.AcmeTrader.GetAsync($"/api/bo/cfg/effective?tournamentId={f.TournamentId}&marketTypeId={f.MarketTypeId}&keys=margin.pct"));
        Assert.Equal(0.04m, after[0].GetProperty("value").GetDecimal());
        Assert.Equal("tournament", after[0].GetProperty("winner").GetProperty("scopeType").GetString());

        var audit = await Json(await f.AcmeAdmin.GetAsync($"/api/bo/adm/audit?entityType=setting_change_set&entityId={id}"));
        Assert.Equal(["cfg.change_set.applied", "cfg.change_set.approved", "cfg.change_set.submitted"],
            audit.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("action").GetString()));
    }

    [DbFact]
    public async Task Invalid_changes_are_rejected_with_a_reason()
    {
        Assert.Equal("INVALID_SETTING", await ProblemCode(await f.AcmeTrader.PostAsJsonAsync("/api/bo/cfg/change-sets",
            ChangeSet("Too much", "margin.pct", 0.9m)), HttpStatusCode.BadRequest));
        Assert.Equal("UNKNOWN_KEY", await ProblemCode(await f.AcmeTrader.PostAsJsonAsync("/api/bo/cfg/change-sets",
            ChangeSet("Nope", "no.such.key", 1)), HttpStatusCode.BadRequest));
        Assert.Equal("UNKNOWN_SCOPE", await ProblemCode(await f.AcmeTrader.PostAsJsonAsync("/api/bo/cfg/change-sets",
            ChangeSet("Missing tournament", "offer.visible", false, scopeId: 999999)), HttpStatusCode.BadRequest));
        Assert.Equal("PLATFORM_ONLY", await ProblemCode(await f.AcmeAdmin.PostAsJsonAsync("/api/bo/cfg/change-sets", new
        {
            title = "Operator user on platform scope",
            platform = true,
            changes = new[] { new { op = "upsert", scopeType = "platform", scopeId = (long?)null, marketTypeId = (int?)null, key = "odds.max", value = 500 } },
        }), HttpStatusCode.Forbidden));
    }

    [DbFact]
    public async Task Platform_staff_impersonate_read_only_unless_allowed()
    {
        var support = f.Platform(operatorId: 1, user: DevSeed.PlatformSupport);
        var me = await Json(await support.GetAsync("/api/bo/me"));
        Assert.True(me.GetProperty("impersonating").GetBoolean());
        Assert.True(me.GetProperty("readOnly").GetBoolean());
        await Json(await support.GetAsync("/api/bo/cfg/change-sets"));
        Assert.Equal("PERMISSION_DENIED", await ProblemCode(await support.PostAsJsonAsync("/api/bo/cfg/change-sets",
            ChangeSet("Support edit", "offer.visible", false)), HttpStatusCode.Forbidden));

        // The super admin holds impersonate_write and can act; platform-wide settings need platform scope.
        var admin = f.Platform(operatorId: 1);
        await Json(await admin.PostAsJsonAsync("/api/bo/cfg/change-sets", ChangeSet("Admin edit", "offer.live_enabled", false, scope: "sport", scopeId: f.SportId)), HttpStatusCode.Created);
        var platformSet = await Json(await f.Platform().PostAsJsonAsync("/api/bo/cfg/change-sets", new
        {
            title = "Cap odds platform-wide",
            platform = true,
            changes = new[] { new { op = "upsert", scopeType = "platform", scopeId = (long?)null, marketTypeId = (int?)null, key = "odds.max", value = 500 } },
        }), HttpStatusCode.Created);
        Assert.Null(platformSet.GetProperty("operatorId").GetString());

        // Platform row reaches both operators.
        foreach (var client in new[] { f.AcmeTrader, f.BetgeoAdmin })
        {
            var e = await Json(await client.GetAsync($"/api/bo/cfg/effective?tournamentId={f.OtherTournamentId}&keys=odds.max"));
            Assert.Equal(500, e[0].GetProperty("value").GetInt32());
        }
    }

    [DbFact]
    public async Task Users_roles_and_last_admin_protection()
    {
        var admin = f.AcmeAdmin;
        var created = await Json(await admin.PostAsJsonAsync("/api/bo/adm/users",
            new { username = "acme-support", email = "support@acme.test", displayName = "Acme Support", roles = new[] { "customer_support" } }), HttpStatusCode.Created);
        var id = created.GetProperty("user").GetProperty("id").GetGuid();
        Assert.Equal(1, created.GetProperty("user").GetProperty("operatorId").GetInt64());

        // Platform roles cannot be granted by an operator.
        Assert.Equal("UNKNOWN_ROLE", await ProblemCode(await admin.PutAsJsonAsync($"/api/bo/adm/users/{id}/roles",
            new { roles = new[] { "platform_superadmin" }, reason = "escalation attempt" }), HttpStatusCode.BadRequest));
        var updated = await Json(await admin.PutAsJsonAsync($"/api/bo/adm/users/{id}/roles", new { roles = new[] { "auditor", "finance" }, reason = "moved to finance" }));
        Assert.Equal(["auditor", "finance"], updated.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        // BetGeo cannot touch Acme users.
        Assert.Equal(HttpStatusCode.NotFound, (await f.BetgeoAdmin.PostAsJsonAsync($"/api/bo/adm/users/{id}/disable", new { reason = "not yours" })).StatusCode);

        // The last operator admin keeps the role.
        Assert.Equal("LAST_ADMIN", await ProblemCode(await f.Platform(1).PutAsJsonAsync($"/api/bo/adm/users/{DevSeed.AcmeAdmin}/roles",
            new { roles = new[] { "trader" }, reason = "demote the only admin" }), HttpStatusCode.Conflict));

        var disabled = await Json(await admin.PostAsJsonAsync($"/api/bo/adm/users/{id}/disable", new { reason = "left the company" }));
        Assert.Equal("disabled", disabled.GetProperty("status").GetString());
        Assert.Equal("USER_DISABLED", await ProblemCode(await f.Operator(id, "acmebet").GetAsync("/api/bo/me"), HttpStatusCode.Forbidden));
    }

    [DbFact]
    public async Task Platform_creates_operators_with_a_default_brand_and_modules()
    {
        var platform = f.Platform();
        Assert.Equal("PLATFORM_ONLY", await ProblemCode(await f.AcmeAdmin.GetAsync("/api/bo/platform/operators"), HttpStatusCode.Forbidden));

        var op = await Json(await platform.PostAsJsonAsync("/api/bo/platform/operators",
            new { code = "newbet", name = "NewBet", baseCurrency = "GEL", currencies = new[] { "GEL", "EUR" } }), HttpStatusCode.Created);
        var id = op.GetProperty("id").GetInt64();
        Assert.Equal(["GEL", "EUR"], op.GetProperty("currencies").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync("/api/bo/platform/operators", new { code = "newbet", name = "Again" })).StatusCode);

        var asOperator = f.Platform(id);
        var brands = await Json(await asOperator.GetAsync("/api/bo/brands"));
        Assert.Equal("newbet", brands[0].GetProperty("code").GetString());
        await Json(await asOperator.PostAsJsonAsync("/api/bo/brands", new { code = "newbet-com", name = "NewBet International", audience = "foreign" }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await asOperator.PostAsJsonAsync("/api/bo/brands", new { code = "newbet-com", name = "dup" })).StatusCode);
        var modules = await Json(await asOperator.GetAsync("/api/bo/modules"));
        Assert.True(modules.GetProperty("CFG").GetBoolean());
    }
}
