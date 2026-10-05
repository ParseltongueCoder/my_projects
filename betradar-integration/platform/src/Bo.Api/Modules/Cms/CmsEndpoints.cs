using Bo.Api.Infrastructure;
using Bo.Api.Modules.Catalog;
using Bo.Api.Modules.Config;
using Bo.Core.Cms;
using Bo.Core.Config;
using Dapper;
using Npgsql;

namespace Bo.Api.Modules.Cms;

/// <summary>A player message as the bet / cash-out API returns it (docs/06 §6.3): code and params always, plus the text.</summary>
public sealed record RenderedMessage(string Code, IReadOnlyDictionary<string, string> Params, string Title, string Message, string Lang, string Source);

/// <summary>
/// Texts of reason codes with the fallback of docs/06 §6.2 and the brand dimension of docs/09 §2.16: for each language
/// of the chain (requested, then <c>i18n.fallback_langs</c>): brand text → operator text → platform text → our default;
/// finally the English default.
/// </summary>
public sealed class CmsMessages(SettingsSnapshotCache settings, Names names)
{
    public sealed record TextRow(long? OperatorId, long? BrandId, string Code, string Field, string Lang, string Text);

    public static async Task<IReadOnlyList<TextRow>> TextsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long? brandId, string[]? codes = null) =>
        // RLS: this operator's rows and platform rows only.
        (await conn.QueryAsync<TextRow>("""
            SELECT operator_id AS operatorId, brand_id AS brandId, entity_id AS code, field, lang, text FROM bo.translation
            WHERE entity_type = 'message' AND (brand_id IS NULL OR brand_id = @brandId) AND (@codes::text[] IS NULL OR entity_id = ANY(@codes))
            """, new { brandId, codes }, tx)).ToList();

    /// <summary>The effective text of one field and where it came from (brand | operator | platform | default).</summary>
    public static (string Text, string Source, string Lang) Pick(MessageDef def, IReadOnlyList<TextRow> rows, string field, IReadOnlyList<string> langs)
    {
        foreach (var lang in langs)
        {
            var mine = rows.Where(r => r.Code == def.Code && r.Field == field && r.Lang == lang).ToList();
            if (mine.FirstOrDefault(r => r.BrandId is not null) is { } brand)
            {
                return (brand.Text, "brand", lang);
            }
            if (mine.FirstOrDefault(r => r.OperatorId is not null && r.BrandId is null) is { } op)
            {
                return (op.Text, "operator", lang);
            }
            if (mine.FirstOrDefault(r => r.OperatorId is null) is { } platform)
            {
                return (platform.Text, "platform", lang);
            }
            if (def.Default(lang) is { } d)
            {
                return (field == "title" ? d.Title : d.Text, "default", lang);
            }
        }
        return (field == "title" ? def.En.Title : def.En.Text, "default", "en");
    }

    public async Task<RenderedMessage> RenderAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long operatorId, long? brandId, string code,
        string lang, IReadOnlyDictionary<string, string> values)
    {
        var def = MessageCatalog.ByCode.GetValueOrDefault(code) ?? MessageCatalog.ByCode["SYS_UNAVAILABLE"];
        var langs = await names.LanguageChainAsync(conn, tx, operatorId, lang);
        var rows = await TextsAsync(conn, tx, brandId, [def.Code]);
        var title = Pick(def, rows, "title", langs);
        var text = Pick(def, rows, "text", langs);
        var message = MessageFormat.Render(text.Text, values);
        var showCode = SettingResolver.Resolve(SettingCatalog.ByKey["cms.show_technical_codes"], await settings.GetAsync(conn, tx, operatorId),
            new ScopeContext(operatorId, brandId)).Value!.GetValue<bool>();
        return new RenderedMessage(def.Code, values, MessageFormat.Render(title.Text, values), showCode ? $"{message} [{def.Code}]" : message, text.Lang, text.Source);
    }
}

/// <summary>CMS (docs/06 §6): reason-code texts per language and brand, with lint and preview.</summary>
public static class CmsEndpoints
{
    public sealed record TextEdit(string? Title, string? Text);

    /// <summary>Per language: a value sets the text, an empty string removes it (back to the inherited one), null leaves it.</summary>
    public sealed record SaveRequest(Dictionary<string, TextEdit> Texts, long? BrandId, bool Platform = false);

    public sealed record Cell(string? Brand, string? Operator, string? Platform, string? Default, string Effective, string Source);

    public static RouteGroupBuilder MapCms(this RouteGroupBuilder api)
    {
        var cms = api.MapGroup("/cms");

        cms.MapGet("/messages", (TenantContext t, BoDb db, string? category, string? q, long? brandId, bool? missing, CancellationToken ct) =>
        {
            t.Require("cms.view");
            return db.TenantAsync(t, async (conn, tx) =>
            {
                await EnsureBrandAsync(conn, tx, brandId);
                var langs = await LanguagesAsync(conn, tx, t);
                var rows = await CmsMessages.TextsAsync(conn, tx, brandId);
                var term = q?.Trim();
                var items = MessageCatalog.All
                    .Where(d => category is null || d.Category == category)
                    .Where(d => string.IsNullOrEmpty(term) || d.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
                                || d.En.Text.Contains(term, StringComparison.OrdinalIgnoreCase)
                                || rows.Any(r => r.Code == d.Code && r.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .Select(d => new
                    {
                        d.Code,
                        d.Module,
                        d.Category,
                        d.Severity,
                        d.Params,
                        d.CustomerVisible,
                        d.Description,
                        texts = langs.ToDictionary(l => l, l => new
                        {
                            title = CellFor(d, rows, "title", l),
                            text = CellFor(d, rows, "text", l),
                        }),
                    })
                    .Where(m => missing != true || m.texts.Values.Any(x => x.text.Source == "missing"))
                    .ToList();
                return new { languages = langs, items };
            }, ct);
        });

        cms.MapPut("/messages/{code}", (TenantContext t, BoDb db, string code, SaveRequest body, CancellationToken ct) =>
        {
            t.Require("cms.edit");
            var def = MessageCatalog.ByCode.GetValueOrDefault(code) ?? throw BoProblem.NotFound("Message code");
            long? operatorId;
            if (body.Platform)
            {
                t.RequirePlatform();
                if (body.BrandId is not null)
                {
                    throw BoProblem.Invalid("BAD_SCOPE", "Platform texts are for every brand");
                }
                operatorId = null;
            }
            else
            {
                operatorId = t.RequireOperator();
            }
            if (body.Texts.Count == 0)
            {
                throw BoProblem.Invalid("TEXTS_REQUIRED", "Send at least one language");
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                await EnsureBrandAsync(conn, tx, body.BrandId);
                var allowed = body.Platform
                    ? (await conn.QueryAsync<string>("SELECT code FROM bo.language", transaction: tx)).ToList()
                    : await LanguagesAsync(conn, tx, t);
                var before = await CmsMessages.TextsAsync(conn, tx, body.BrandId, [code]);
                var changes = new List<object>();
                foreach (var (lang, edit) in body.Texts)
                {
                    if (!allowed.Contains(lang))
                    {
                        throw BoProblem.Invalid("BAD_LANGUAGE", $"Language '{lang}' is not enabled for this operator");
                    }
                    foreach (var (field, value, max) in new[] { ("title", edit.Title, 100), ("text", edit.Text, 500) })
                    {
                        if (value is null)
                        {
                            continue;
                        }
                        var text = value.Trim();
                        if (text.Length > max)
                        {
                            throw BoProblem.Invalid("TOO_LONG", $"{lang} {field}: at most {max} characters");
                        }
                        if (text.Length > 0 && MessageFormat.Lint(text, def.Params, def.CustomerVisible) is { } lint)
                        {
                            throw BoProblem.Invalid("MESSAGE_LINT", $"{lang} {field}: {lint}");
                        }
                        if (text.Length == 0)
                        {
                            await conn.ExecuteAsync("""
                                DELETE FROM bo.translation WHERE operator_id IS NOT DISTINCT FROM @operatorId AND brand_id IS NOT DISTINCT FROM @BrandId
                                  AND entity_type = 'message' AND entity_id = @code AND field = @field AND lang = @lang
                                """, new { operatorId, body.BrandId, code, field, lang }, tx);
                        }
                        else
                        {
                            await conn.ExecuteAsync("""
                                INSERT INTO bo.translation (operator_id, brand_id, entity_type, entity_id, field, lang, text, updated_by)
                                VALUES (@operatorId, @BrandId, 'message', @code, @field, @lang, @text, @user)
                                ON CONFLICT (operator_id, brand_id, entity_type, entity_id, field, lang) DO UPDATE SET
                                  text = EXCLUDED.text, updated_by = EXCLUDED.updated_by, updated_at = now(), version = bo.translation.version + 1
                                """, new { operatorId, body.BrandId, code, field, lang, text, user = t.UserId }, tx);
                        }
                        changes.Add(new { lang, field, text = text.Length == 0 ? null : text });
                    }
                }
                await Audit.WriteAsync(conn, tx, t, "cms.message.saved", "message", code,
                    before.Where(r => r.OperatorId == operatorId && r.BrandId == body.BrandId).Select(r => new { r.Lang, r.Field, r.Text }),
                    new { body.BrandId, changes }, operatorId: operatorId);
                await Audit.OutboxAsync(conn, tx, operatorId, $"bo.changed.{operatorId?.ToString() ?? "platform"}.cms", new { code, body.BrandId });
                return Results.NoContent();
            }, ct);
        });

        // Preview with sample parameters: params=maxStake:150,currency:GEL
        cms.MapGet("/messages/{code}/preview", (TenantContext t, BoDb db, CmsMessages messages, string code, string? lang, long? brandId,
            string? @params, CancellationToken ct) =>
        {
            t.Require("cms.view");
            var operatorId = t.RequireOperator();
            var def = MessageCatalog.ByCode.GetValueOrDefault(code) ?? throw BoProblem.NotFound("Message code");
            var values = (@params ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            foreach (var p in def.Params.Where(p => !values.ContainsKey(p)))
            {
                values[p] = Sample(p);
            }
            return db.TenantAsync(t, async (conn, tx) =>
            {
                await EnsureBrandAsync(conn, tx, brandId);
                return await messages.RenderAsync(conn, tx, operatorId, brandId, code, lang ?? "ka", values);
            }, ct);
        });

        return api;
    }

    private static Cell CellFor(MessageDef def, IReadOnlyList<CmsMessages.TextRow> rows, string field, string lang)
    {
        var mine = rows.Where(r => r.Code == def.Code && r.Field == field && r.Lang == lang).ToList();
        var dflt = def.Default(lang) is { } d ? (field == "title" ? d.Title : d.Text) : null;
        var brand = mine.FirstOrDefault(r => r.BrandId is not null)?.Text;
        var op = mine.FirstOrDefault(r => r.OperatorId is not null && r.BrandId is null)?.Text;
        var platform = mine.FirstOrDefault(r => r.OperatorId is null)?.Text;
        var (effective, source) = brand is not null ? (brand, "brand")
            : op is not null ? (op, "operator")
            : platform is not null ? (platform, "platform")
            : dflt is not null ? (dflt, "default")
            : (field == "title" ? def.En.Title : def.En.Text, "missing");
        return new Cell(brand, op, platform, dflt, effective, source);
    }

    private static string Sample(string param) => param switch
    {
        "currency" => "GEL",
        "newOdds" or "odds" => "2.35",
        "seconds" => "30",
        "minAge" => "25",
        "maxSelections" => "20",
        _ => "150",
    };

    private static async Task<List<string>> LanguagesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantContext t) =>
        t.OperatorId is { } op
            ? [.. await conn.ExecuteScalarAsync<string[]>("SELECT languages FROM bo.operator WHERE id = @op", new { op }, tx) ?? []]
            : ["ka", "en", "ru"];

    private static async Task EnsureBrandAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long? brandId)
    {
        // RLS: own brands only, so another operator's brand id is simply not found.
        if (brandId is { } id && !await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM bo.brand WHERE id = @id)", new { id }, tx))
        {
            throw BoProblem.NotFound("Brand");
        }
    }
}
