using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bo.Api.Modules.I18n;
using Platform.Tests;

namespace Bo.Tests;

public sealed class CatalogApiTests(BoApiFixture f) : IClassFixture<BoApiFixture>
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static async Task<JsonElement> Json(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode} {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement Node(JsonElement tree, string type, long id)
    {
        foreach (var n in tree.EnumerateArray())
        {
            if (n.GetProperty("type").GetString() == type && n.GetProperty("id").GetInt64() == id)
            {
                return n;
            }
            if (n.GetProperty("children").GetArrayLength() > 0 && Find(n.GetProperty("children"), type, id) is { } found)
            {
                return found;
            }
        }
        throw new InvalidOperationException($"{type} {id} not in tree");
    }

    private static JsonElement? Find(JsonElement nodes, string type, long id)
    {
        try
        {
            return Node(nodes, type, id);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Task<JsonElement> TreeAsync(HttpClient c, string lang = "ka") => c.GetAsync($"/api/bo/cat/tree?lang={lang}").ContinueWith(r => Json(r.Result)).Unwrap();

    [DbFact]
    public async Task Names_fall_back_operator_then_platform_then_provider_per_operator()
    {
        // Platform default for everyone, then an AcmeBet-only name.
        await Json(await f.Platform().PutAsJsonAsync("/api/bo/i18n/translations", new
        {
            items = new[] { new { entityType = "tournament", entityId = f.TournamentId.ToString(), field = "name", lang = "ka", text = "ეროვნული ლიგა", platform = true } },
        }));
        await Json(await f.AcmeAdmin.PutAsJsonAsync("/api/bo/i18n/translations", new
        {
            items = new[] { new { entityType = "tournament", entityId = f.TournamentId.ToString(), field = "name", lang = "ka", text = "Crystalbet ეროვნული ლიგა", platform = false } },
        }));

        var acme = Node(await TreeAsync(f.AcmeAdmin), "tournament", f.TournamentId);
        Assert.Equal("Crystalbet ეროვნული ლიგა", acme.GetProperty("name").GetString());
        Assert.Equal("operator", acme.GetProperty("nameSource").GetString());
        Assert.Equal("Erovnuli Liga", acme.GetProperty("feedName").GetString());

        var betgeo = Node(await TreeAsync(f.BetgeoAdmin), "tournament", f.TournamentId);
        Assert.Equal("ეროვნული ლიგა", betgeo.GetProperty("name").GetString());
        Assert.Equal("platform", betgeo.GetProperty("nameSource").GetString());

        // No Russian anywhere → fallback language (en) from the provider.
        var ru = Node(await TreeAsync(f.BetgeoAdmin, "ru"), "tournament", f.TournamentId);
        Assert.Equal("Erovnuli Liga", ru.GetProperty("name").GetString());
        Assert.Equal("provider", ru.GetProperty("nameSource").GetString());
    }

    [DbFact]
    public async Task Hiding_a_country_hides_its_leagues_for_that_operator_only_and_order_is_per_operator()
    {
        var set = await Json(await f.AcmeAdmin.PostAsJsonAsync("/api/bo/cat/visibility", new
        {
            targets = new[] { new { type = "category", id = f.CategoryId } }, visible = false, reason = "not licensed",
        }));
        Assert.Equal("applied", set.GetProperty("status").GetString());

        var acmeTree = await TreeAsync(f.AcmeAdmin);
        Assert.True(Node(acmeTree, "category", f.CategoryId).GetProperty("hiddenHere").GetBoolean());
        Assert.False(Node(acmeTree, "tournament", f.TournamentId).GetProperty("visible").GetBoolean());
        Assert.False(Node(acmeTree, "tournament", f.TournamentId).GetProperty("hiddenHere").GetBoolean());
        Assert.True(Node(await TreeAsync(f.BetgeoAdmin), "tournament", f.TournamentId).GetProperty("visible").GetBoolean());

        await Json(await f.AcmeAdmin.PutAsJsonAsync("/api/bo/cat/order", new { type = "tournament", ids = new[] { f.OtherTournamentId, f.TournamentId } }),
            HttpStatusCode.NoContent);
        await Json(await f.AcmeAdmin.PatchAsJsonAsync($"/api/bo/cat/nodes/tournament/{f.TournamentId}", new { isTop = true, slug = "erovnuli-liga" }));
        var children = Node(await TreeAsync(f.AcmeAdmin), "category", f.CategoryId).GetProperty("children").EnumerateArray().Select(c => c.GetProperty("id").GetInt64()).ToList();
        Assert.Equal([f.OtherTournamentId, f.TournamentId], children);
        Assert.True(Node(await TreeAsync(f.AcmeAdmin), "tournament", f.TournamentId).GetProperty("isTop").GetBoolean());
        Assert.False(Node(await TreeAsync(f.BetgeoAdmin), "tournament", f.TournamentId).GetProperty("isTop").GetBoolean());

        // Showing again removes the row; the league inherits visibility again.
        await Json(await f.AcmeAdmin.PostAsJsonAsync("/api/bo/cat/visibility", new
        {
            targets = new[] { new { type = "category", id = f.CategoryId } }, visible = true, reason = "licence granted",
        }));
        Assert.True(Node(await TreeAsync(f.AcmeAdmin), "tournament", f.TournamentId).GetProperty("visible").GetBoolean());

        // Content managers (no cfg.edit) cannot change visibility: it is a configuration change set.
        var cm = await Json(await f.AcmeAdmin.PostAsJsonAsync("/api/bo/adm/users",
            new { username = "acme-content", email = "content@acme.test", displayName = "Acme Content", roles = new[] { "content_manager" } }), HttpStatusCode.Created);
        var content = f.Operator(cm.GetProperty("user").GetProperty("id").GetGuid(), "acmebet");
        Assert.Equal(HttpStatusCode.Forbidden, (await content.PostAsJsonAsync("/api/bo/cat/visibility",
            new { targets = new[] { new { type = "category", id = f.CategoryId } }, visible = false, reason = "try" })).StatusCode);
        await Json(await content.PatchAsJsonAsync($"/api/bo/cat/nodes/sport/{f.SportId}", new { sortOrder = 1 }));
    }

    [DbFact]
    public async Task Templates_are_linted_and_previewed_in_the_operators_language()
    {
        var outcome = I18nEndpoints.OutcomeKey(f.TotalMarketTypeId, "", "12");
        var bad = await f.AcmeAdmin.PutAsJsonAsync("/api/bo/i18n/translations", new
        {
            items = new[] { new { entityType = "outcome_type", entityId = outcome, field = "template", lang = "ka", text = "მეტი {totl}", platform = false } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("missing {total}", await bad.Content.ReadAsStringAsync());

        await Json(await f.AcmeAdmin.PutAsJsonAsync("/api/bo/i18n/translations", new
        {
            items = new object[]
            {
                new { entityType = "market_type", entityId = f.TotalMarketTypeId.ToString(), field = "template", lang = "ka", text = "ტოტალი", platform = false },
                new { entityType = "outcome_type", entityId = outcome, field = "template", lang = "ka", text = "მეტი {total}", platform = false },
            },
        }));
        var preview = await Json(await f.AcmeAdmin.GetAsync($"/api/bo/i18n/preview?marketTypeId={f.TotalMarketTypeId}&lang=ka&specifiers=total=2.5"));
        Assert.Equal("ტოტალი", preview.GetProperty("market").GetProperty("rendered").GetString());
        var outcomes = preview.GetProperty("outcomes").EnumerateArray().ToList();
        Assert.Equal("მეტი 2.5", outcomes[0].GetProperty("rendered").GetString());
        Assert.Equal("under 2.5", outcomes[1].GetProperty("rendered").GetString()); // untranslated → English provider template

        var grid = await Json(await f.AcmeAdmin.GetAsync("/api/bo/i18n/translations?entityType=outcome_type&langs=ka,en&missing=true"));
        var ids = grid.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()).ToList();
        Assert.DoesNotContain(outcome, ids);
        Assert.Contains(I18nEndpoints.OutcomeKey(f.TotalMarketTypeId, "", "13"), ids);

        // Operator languages only.
        Assert.Contains("BAD_LANGUAGE", await (await f.AcmeAdmin.PutAsJsonAsync("/api/bo/i18n/translations", new
        {
            items = new[] { new { entityType = "sport", entityId = f.SportId.ToString(), field = "name", lang = "tr", text = "Futbol", platform = false } },
        })).Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task Csv_export_and_import_with_a_dry_run()
    {
        var csv = await (await f.AcmeAdmin.GetAsync("/api/bo/i18n/export?entityType=competitor&langs=ka")).Content.ReadAsStringAsync();
        var lines = I18nEndpoints.ParseCsv(csv);
        Assert.Equal(["entity_type", "entity_id", "field", "lang", "source_text", "text"], lines[0]);
        Assert.Contains(lines, l => l.Count == 6 && l[1] == f.HomeId.ToString() && l[4] == "Dinamo Tbilisi");

        var import = $"entity_type,entity_id,field,lang,source_text,text\ncompetitor,{f.HomeId},name,ka,Dinamo Tbilisi,\"დინამო, თბილისი\"\n";
        var dry = await Json(await f.AcmeAdmin.PostAsync("/api/bo/i18n/import?dryRun=true", new StringContent(import, Encoding.UTF8, "text/csv")));
        Assert.Equal(1, dry.GetProperty("changes").GetArrayLength());
        var applied = await Json(await f.AcmeAdmin.PostAsync("/api/bo/i18n/import?dryRun=false", new StringContent(import, Encoding.UTF8, "text/csv")));
        Assert.Equal(1, applied.GetProperty("saved").GetInt32());

        var participants = await Json(await f.AcmeAdmin.GetAsync("/api/bo/cat/participants?q=Dinamo&lang=ka"));
        Assert.Equal("დინამო, თბილისი", participants.GetProperty("items")[0].GetProperty("name").GetString());
        var events = await Json(await f.AcmeAdmin.GetAsync("/api/bo/cat/events?lang=ka"));
        Assert.Contains(events.GetProperty("items").EnumerateArray(), e => e.GetProperty("name").GetString() == "დინამო, თბილისი v Torpedo Kutaisi");
        // Another operator still sees the provider name.
        var other = await Json(await f.BetgeoAdmin.GetAsync("/api/bo/cat/participants?q=Dinamo&lang=ka"));
        Assert.Equal("Dinamo Tbilisi", other.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [DbFact]
    public async Task Event_overrides_are_per_operator_with_optimistic_locking()
    {
        var saved = await Json(await f.AcmeAdmin.PutAsJsonAsync($"/api/bo/cat/events/{f.EventId}/override",
            new { isFeatured = true, featuredOrder = 1, note = "derby" }));
        Assert.Equal(1, saved.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await f.AcmeAdmin.PutAsJsonAsync($"/api/bo/cat/events/{f.EventId}/override",
            new { isFeatured = false, version = 7 })).StatusCode);

        var featured = await Json(await f.AcmeAdmin.GetAsync("/api/bo/cat/events?featured=true"));
        Assert.Single(featured.GetProperty("items").EnumerateArray());
        var notFeatured = await Json(await f.BetgeoAdmin.GetAsync("/api/bo/cat/events?featured=true"));
        Assert.Empty(notFeatured.GetProperty("items").EnumerateArray());

        var detail = await Json(await f.AcmeAdmin.GetAsync($"/api/bo/cat/events/{f.EventId}"));
        Assert.True(detail.GetProperty("override").GetProperty("isFeatured").GetBoolean());
        Assert.Equal(2, detail.GetProperty("competitors").GetArrayLength());
        Assert.True(detail.GetProperty("policy").GetProperty("offer.visible").GetBoolean());
    }

    [DbFact]
    public async Task Images_upload_link_with_operator_over_platform_and_reject_unsafe_svg()
    {
        static MultipartFormDataContent File(byte[] bytes, string name)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); // type is sniffed, not trusted
            return new MultipartFormDataContent { { content, "file", name } };
        }

        var platformImage = await Json(await f.Platform().PostAsync("/api/bo/media", File(Png, "flag.png")));
        Assert.Equal("image/png", platformImage.GetProperty("mime").GetString());
        await Json(await f.Platform().PutAsJsonAsync("/api/bo/media/links", new
        {
            entityType = "category", entityId = f.CategoryId, role = "flag", mediaId = platformImage.GetProperty("id").GetGuid(), platform = true,
        }), HttpStatusCode.NoContent);

        var acmeImage = await Json(await f.AcmeAdmin.PostAsync("/api/bo/media", File([.. Png, 0], "acme-flag.png")));
        await Json(await f.AcmeAdmin.PutAsJsonAsync("/api/bo/media/links", new
        {
            entityType = "category", entityId = f.CategoryId, role = "flag", mediaId = acmeImage.GetProperty("id").GetGuid(),
        }), HttpStatusCode.NoContent);

        var acmeFlag = Node(await TreeAsync(f.AcmeAdmin), "category", f.CategoryId).GetProperty("media")[0];
        Assert.Equal(acmeImage.GetProperty("id").GetGuid(), acmeFlag.GetProperty("mediaId").GetGuid());
        Assert.False(acmeFlag.GetProperty("inherited").GetBoolean());
        var betgeoFlag = Node(await TreeAsync(f.BetgeoAdmin), "category", f.CategoryId).GetProperty("media")[0];
        Assert.True(betgeoFlag.GetProperty("inherited").GetBoolean());

        var publicGet = await f.Platform().GetAsync(betgeoFlag.GetProperty("url").GetString());
        Assert.Equal("image/png", publicGet.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", publicGet.Headers.CacheControl?.ToString());

        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        Assert.Contains("UNSAFE_SVG", await (await f.AcmeAdmin.PostAsync("/api/bo/media", File(svg, "x.svg"))).Content.ReadAsStringAsync());
        Assert.Contains("BAD_FILE", await (await f.AcmeAdmin.PostAsync("/api/bo/media", File("GIF89a"u8.ToArray(), "x.gif"))).Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("over {total}", "მეტი {total}", null)]
    [InlineData("{$competitor1} ({+hcp})", "({+hcp}) {$competitor1}", null)]
    [InlineData("over {total}", "მეტი", "missing {total}")]
    [InlineData("draw", "ფრე {total}", "unknown {total}")]
    [InlineData("over {total}", "მეტი {total", "Unbalanced braces")]
    public void Template_lint(string source, string translation, string? error)
    {
        var result = I18nEndpoints.LintTemplate(source, translation);
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
    public void Csv_parser_handles_quotes_commas_newlines_and_bom()
    {
        var rows = I18nEndpoints.ParseCsv("﻿a,\"b,1\",\"say \"\"hi\"\"\"\r\n\"multi\nline\",x,\n");
        Assert.Equal(["a", "b,1", "say \"hi\""], rows[0]);
        Assert.Equal(["multi\nline", "x", ""], rows[1]);
    }
}
