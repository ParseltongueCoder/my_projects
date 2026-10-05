using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bo.Api.Infrastructure;
using Bo.Core.Cms;
using Dapper;
using Platform.Tests;

namespace Bo.Tests;

public sealed class CmsApiTests(BoApiFixture f) : IClassFixture<BoApiFixture>
{
    private static async Task<JsonElement> Json(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode} {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string> Code(HttpResponseMessage r, HttpStatusCode expected) =>
        (await Json(r, expected)).GetProperty("code").GetString()!;

    private static async Task<JsonElement> Preview(HttpClient c, string code, string lang, long? brandId = null, string? ps = null) =>
        await Json(await c.GetAsync($"/api/bo/cms/messages/{code}/preview?lang={lang}{(brandId is { } b ? $"&brandId={b}" : "")}{(ps is null ? "" : $"&params={ps}")}"));

    [DbFact]
    public async Task Defaults_render_with_parameters_in_the_players_language()
    {
        var list = await Json(await f.AcmeAdmin.GetAsync("/api/bo/cms/messages?category=bet_reject"));
        var maxStake = list.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("code").GetString() == "LIM_MAX_STAKE_EXCEEDED");
        Assert.Equal(["maxStake", "currency"], maxStake.GetProperty("params").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("default", maxStake.GetProperty("texts").GetProperty("ka").GetProperty("text").GetProperty("source").GetString());
        // Russian has no default text yet: it shows as missing in the grid.
        Assert.Equal("missing", maxStake.GetProperty("texts").GetProperty("ru").GetProperty("text").GetProperty("source").GetString());

        var ka = await Preview(f.AcmeAdmin, "LIM_MAX_STAKE_EXCEEDED", "ka", ps: "maxStake:150,currency:GEL");
        Assert.Equal("მაქსიმალური ფსონი ამ არჩევანზე არის 150.00 GEL.", ka.GetProperty("message").GetString());
        Assert.Equal("LIM_MAX_STAKE_EXCEEDED", ka.GetProperty("code").GetString());
        Assert.Equal("150", ka.GetProperty("params").GetProperty("maxStake").GetString());
        // No Russian text: the fallback language (i18n.fallback_langs = en).
        var ru = await Preview(f.AcmeAdmin, "LIM_MAX_STAKE_EXCEEDED", "ru", ps: "maxStake:99.5,currency:USD");
        Assert.Equal("The maximum stake on this selection is 99.50 USD.", ru.GetProperty("message").GetString());
        Assert.Equal("en", ru.GetProperty("lang").GetString());
    }

    [DbFact]
    public async Task Brand_then_operator_then_platform_text_wins_per_operator()
    {
        const string code = "INT_INSUFFICIENT_FUNDS";
        await Json(await f.Platform().PutAsJsonAsync($"/api/bo/cms/messages/{code}", new
        {
            platform = true, texts = new Dictionary<string, object> { ["ka"] = new { text = "ანგარიშზე საკმარისი თანხა არ არის." } },
        }), HttpStatusCode.NoContent);
        await Json(await f.AcmeAdmin.PutAsJsonAsync($"/api/bo/cms/messages/{code}", new
        {
            texts = new Dictionary<string, object> { ["ka"] = new { title = "თანხა არ კმარა", text = "შეავსეთ ბალანსი AcmeBet-ზე." } },
        }), HttpStatusCode.NoContent);
        var brands = await Json(await f.AcmeAdmin.GetAsync("/api/bo/brands"));
        var foreign = brands.EnumerateArray().Single(b => b.GetProperty("code").GetString() == "acmebet-com").GetProperty("id").GetInt64();
        await Json(await f.AcmeAdmin.PutAsJsonAsync($"/api/bo/cms/messages/{code}", new
        {
            brandId = foreign, texts = new Dictionary<string, object> { ["en"] = new { text = "Please top up your AcmeBet International wallet." } },
        }), HttpStatusCode.NoContent);

        Assert.Equal("შეავსეთ ბალანსი AcmeBet-ზე.", (await Preview(f.AcmeAdmin, code, "ka")).GetProperty("message").GetString());
        Assert.Equal("თანხა არ კმარა", (await Preview(f.AcmeAdmin, code, "ka")).GetProperty("title").GetString());
        Assert.Equal("Please top up your AcmeBet International wallet.", (await Preview(f.AcmeAdmin, code, "en", foreign)).GetProperty("message").GetString());
        Assert.Equal("brand", (await Preview(f.AcmeAdmin, code, "en", foreign)).GetProperty("source").GetString());
        // The local brand has no English brand text: operator → platform → default.
        Assert.Equal("Your balance is too low for this stake.", (await Preview(f.AcmeAdmin, code, "en")).GetProperty("message").GetString());

        var betgeo = await Preview(f.BetgeoAdmin, code, "ka");
        Assert.Equal("ანგარიშზე საკმარისი თანხა არ არის.", betgeo.GetProperty("message").GetString());
        Assert.Equal("platform", betgeo.GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await f.BetgeoAdmin.GetAsync($"/api/bo/cms/messages/{code}/preview?lang=en&brandId={foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.BetgeoAdmin.PutAsJsonAsync($"/api/bo/cms/messages/{code}", new
        {
            brandId = foreign, texts = new Dictionary<string, object> { ["en"] = new { text = "hijack" } },
        })).StatusCode);

        // An empty text removes the override: back to the platform text.
        await Json(await f.AcmeAdmin.PutAsJsonAsync($"/api/bo/cms/messages/{code}", new
        {
            texts = new Dictionary<string, object> { ["ka"] = new { title = "", text = "" } },
        }), HttpStatusCode.NoContent);
        Assert.Equal("platform", (await Preview(f.AcmeAdmin, code, "ka")).GetProperty("source").GetString());

        // Database level: BetGeo's tenant sees platform rows only.
        var rows = await new BoDb(f.Db.DataSource).TenantAsync(TenantContext.System(new OperatorRef(2, "betgeo", "BetGeo")), (conn, tx) =>
            conn.QueryAsync<long?>("SELECT operator_id FROM bo.translation WHERE entity_type = 'message'", transaction: tx));
        Assert.All(rows, op => Assert.Null(op));

        var audit = await Json(await f.AcmeAdmin.GetAsync($"/api/bo/adm/audit?entityType=message&entityId={code}"));
        Assert.Equal(3, audit.GetProperty("items").GetArrayLength());
    }

    [DbFact]
    public async Task Texts_are_linted_for_parameters_internal_terms_and_languages()
    {
        Assert.Contains("Unknown parameter {limit}", await (await f.AcmeAdmin.PutAsJsonAsync("/api/bo/cms/messages/LIM_MAX_STAKE_EXCEEDED", new
        {
            texts = new Dictionary<string, object> { ["en"] = new { text = "Max {limit}" } },
        })).Content.ReadAsStringAsync());
        Assert.Contains("internal terms", await (await f.AcmeAdmin.PutAsJsonAsync("/api/bo/cms/messages/LIM_NOT_ACCEPTED", new
        {
            texts = new Dictionary<string, object> { ["en"] = new { text = "Liability limit reached" } },
        })).Content.ReadAsStringAsync());
        Assert.Equal("BAD_LANGUAGE", await Code(await f.AcmeAdmin.PutAsJsonAsync("/api/bo/cms/messages/LIM_NOT_ACCEPTED", new
        {
            texts = new Dictionary<string, object> { ["tr"] = new { text = "Bahis kabul edilmedi" } },
        }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NotFound, (await f.AcmeAdmin.PutAsJsonAsync("/api/bo/cms/messages/NO_SUCH_CODE", new
        {
            texts = new Dictionary<string, object> { ["en"] = new { text = "x" } },
        })).StatusCode);
        Assert.Equal("PERMISSION_DENIED", await Code(await f.AcmeTrader.PutAsJsonAsync("/api/bo/cms/messages/LIM_NOT_ACCEPTED", new
        {
            texts = new Dictionary<string, object> { ["en"] = new { text = "x" } },
        }), HttpStatusCode.Forbidden));
    }

    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var m in MessageCatalog.All)
        {
            data.Add(m.Code);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Our_default_texts_pass_our_own_lint_and_use_every_parameter(string code)
    {
        var def = MessageCatalog.ByCode[code];
        foreach (var t in new[] { def.En, def.Ka })
        {
            Assert.Null(MessageFormat.Lint(t.Title, def.Params, def.CustomerVisible));
            Assert.Null(MessageFormat.Lint(t.Text, def.Params, def.CustomerVisible));
            var rendered = MessageFormat.Render(t.Text, def.Params.ToDictionary(p => p, _ => "1"));
            Assert.DoesNotContain("{", rendered);
        }
    }

    [Theory]
    [InlineData("Max {maxStake, number} {currency}", "Max 1500.00 GEL")]
    [InlineData("Max {maxStake} {currency}", "Max 1500 GEL")]
    [InlineData("Left {other}", "Left {other}")]
    public void Render_formats_numbers_with_two_decimals(string template, string expected) =>
        Assert.Equal(expected, MessageFormat.Render(template, new Dictionary<string, string> { ["maxStake"] = "1500", ["currency"] = "GEL" }));

    [Theory]
    [InlineData("Max {maxStake, date}", "Unknown format")]
    [InlineData("Max {maxStake", "Unbalanced braces")]
    [InlineData("Max {}", "is not a parameter")]
    public void Lint_rejects_malformed_placeholders(string text, string error) =>
        Assert.Contains(error, MessageFormat.Lint(text, ["maxStake"], true));
}
