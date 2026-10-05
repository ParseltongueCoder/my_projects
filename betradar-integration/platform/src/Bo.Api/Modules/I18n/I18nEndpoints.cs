using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bo.Api.Infrastructure;
using Bo.Api.Modules.Catalog;
using Dapper;
using Npgsql;
using Platform.Canonical.Feed;

namespace Bo.Api.Modules.I18n;

/// <summary>
/// I18N (docs/06 §3): translation grid over feed entities and market/outcome templates, template lint and preview,
/// CSV export/import. Operator rows override platform rows; provider names stay in <c>sb</c>.
/// </summary>
public static partial class I18nEndpoints
{
    public static readonly string[] EntityTypes = ["sport", "category", "tournament", "competitor", "market_type", "outcome_type"];

    public sealed record TranslationEdit(string EntityType, string EntityId, string Field, string Lang, string? Text, bool Platform = false);

    public sealed record SaveRequest(IReadOnlyList<TranslationEdit> Items);

    public sealed record Cell(string? Operator, string? Platform, string? Provider);

    public sealed record GridRow(string EntityType, string EntityId, string Field, string Source, string? Context, Dictionary<string, Cell> Values);

    [GeneratedRegex(@"\{[^{}]+\}")]
    private static partial Regex Placeholder();

    public static RouteGroupBuilder MapI18n(this RouteGroupBuilder api)
    {
        var i18n = api.MapGroup("/i18n");

        i18n.MapGet("/languages", (TenantContext t, BoDb db, CancellationToken ct) =>
        {
            t.Require("i18n.view");
            return db.TenantAsync(t, async (conn, tx) => new
            {
                operatorLanguages = t.OperatorId is { } op
                    ? await conn.ExecuteScalarAsync<string[]>("SELECT languages FROM bo.operator WHERE id = @op", new { op }, tx)
                    : null,
                all = await conn.QueryAsync<(string Code, string Name, string NativeName)>("SELECT code, name, native_name FROM bo.language ORDER BY code", transaction: tx),
            }, ct);
        });

        i18n.MapGet("/translations", (TenantContext t, BoDb db, string entityType, string? q, string? langs, bool? missing, string? field,
            int? page, CancellationToken ct) =>
        {
            t.Require("i18n.view");
            if (!EntityTypes.Contains(entityType))
            {
                throw BoProblem.Invalid("BAD_ENTITY_TYPE", $"Unknown entity type '{entityType}'");
            }
            var languages = (langs ?? "ka,en,ru").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var take = 50;
            var offset = Math.Max(0, (page ?? 1) - 1) * take;
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var sources = await SourcesAsync(conn, tx, entityType, field ?? DefaultField(entityType), q, missing == true ? languages : null,
                    t.OperatorId, offset, take);
                var ids = sources.Select(s => s.EntityId).ToArray();
                var translations = (await conn.QueryAsync<(long? OperatorId, string EntityId, string Lang, string Text)>("""
                    SELECT operator_id, entity_id, lang, text FROM bo.translation
                    WHERE entity_type = @entityType AND field = @field AND entity_id = ANY(@ids) AND brand_id IS NULL
                    """, new { entityType, field = field ?? DefaultField(entityType), ids }, tx)).ToLookup(x => x.EntityId);
                var rows = sources.Select(s =>
                {
                    var provider = Names.Parse(s.ProviderI18n);
                    var values = languages.ToDictionary(l => l, l => new Cell(
                        translations[s.EntityId].FirstOrDefault(x => x.OperatorId is not null && x.Lang == l).Text,
                        translations[s.EntityId].FirstOrDefault(x => x.OperatorId is null && x.Lang == l).Text,
                        provider.GetValueOrDefault(l)));
                    return new GridRow(entityType, s.EntityId, field ?? DefaultField(entityType),
                        provider.GetValueOrDefault("en") ?? provider.Values.FirstOrDefault() ?? s.EntityId, s.Context, values);
                }).ToList();
                return new { items = rows, total = sources.FirstOrDefault()?.Total ?? 0 };
            }, ct);
        });

        i18n.MapPut("/translations", (TenantContext t, BoDb db, SaveRequest body, CancellationToken ct) =>
        {
            t.Require("i18n.edit");
            if (body.Items.Count is 0 or > 500)
            {
                throw BoProblem.Invalid("ITEMS_REQUIRED", "Send 1-500 translations");
            }
            return db.TenantAsync(t, async (conn, tx) => new { saved = await SaveAsync(conn, tx, t, body.Items, "manual") }, ct);
        });

        // Preview of a market type in a language with sample specifiers / competitors (docs/06 §3.5).
        i18n.MapGet("/preview", (TenantContext t, BoDb db, Names names, int marketTypeId, string lang, string? specifiers,
            string? competitor1, string? competitor2, CancellationToken ct) =>
        {
            t.Require("i18n.view");
            var operatorId = t.RequireOperator();
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var md = await conn.QuerySingleOrDefaultAsync<(int Id, string NameI18n)>(
                    "SELECT id, name_template_i18n::text FROM sb.market_description WHERE id = @marketTypeId", new { marketTypeId }, tx);
                if (md == default)
                {
                    throw BoProblem.NotFound("Market type");
                }
                var outcomes = (await conn.QueryAsync<(string Variant, string Code, string NameI18n)>("""
                    SELECT variant, code, name_template_i18n::text FROM sb.market_description_outcome
                    WHERE market_description_id = @marketTypeId ORDER BY ordinal, code
                    """, new { marketTypeId }, tx)).ToList();
                var langs = await names.LanguageChainAsync(conn, tx, operatorId, lang);
                var market = (await names.ResolveAsync(conn, tx, "market_type", new Dictionary<string, string?> { [md.Id.ToString()] = md.NameI18n }, langs, "template"))[md.Id.ToString()];
                var outcomeNames = await names.ResolveAsync(conn, tx, "outcome_type",
                    outcomes.ToDictionary(o => OutcomeKey(marketTypeId, o.Variant, o.Code), o => (string?)o.NameI18n), langs, "template");
                var comps = new[] { competitor1 ?? "Kutaisi Eagles", competitor2 ?? "Rustavi Steel" };
                var spec = UofFeedParser.NormalizeSpecifiers(specifiers);
                return new
                {
                    market = new { template = market.Text, market.Source, rendered = NameRenderer.Render(market.Text, spec, comps) },
                    outcomes = outcomes.Select(o =>
                    {
                        var n = outcomeNames[OutcomeKey(marketTypeId, o.Variant, o.Code)];
                        return new { o.Code, template = n.Text, n.Source, rendered = NameRenderer.Render(n.Text, spec, comps) };
                    }),
                };
            }, ct);
        });

        i18n.MapGet("/export", (TenantContext t, BoDb db, string entityType, string? langs, string? field, CancellationToken ct) =>
        {
            t.Require("i18n.view");
            if (!EntityTypes.Contains(entityType))
            {
                throw BoProblem.Invalid("BAD_ENTITY_TYPE", $"Unknown entity type '{entityType}'");
            }
            var languages = (langs ?? "ka,en,ru").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var f = field ?? DefaultField(entityType);
            return db.TenantAsync(t, async (conn, tx) =>
            {
                var sources = await SourcesAsync(conn, tx, entityType, f, null, null, t.OperatorId, 0, 100_000);
                var own = (await conn.QueryAsync<(string EntityId, string Lang, string Text)>("""
                    SELECT entity_id, lang, text FROM bo.translation
                    WHERE entity_type = @entityType AND field = @f AND brand_id IS NULL AND operator_id IS NOT DISTINCT FROM @op
                    """, new { entityType, f, op = t.OperatorId }, tx)).ToDictionary(x => (x.EntityId, x.Lang), x => x.Text);
                var csv = new StringBuilder("entity_type,entity_id,field,lang,source_text,text\n");
                foreach (var s in sources)
                {
                    var source = Names.Parse(s.ProviderI18n).GetValueOrDefault("en") ?? "";
                    foreach (var l in languages)
                    {
                        csv.AppendJoin(',', Csv(entityType), Csv(s.EntityId), Csv(f), Csv(l), Csv(source), Csv(own.GetValueOrDefault((s.EntityId, l)) ?? "")).Append('\n');
                    }
                }
                return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
                    "text/csv; charset=utf-8", $"translations-{entityType}-{f}.csv");
            }, ct);
        });

        // CSV import: dryRun=true returns the diff; then the same file with dryRun=false applies it.
        i18n.MapPost("/import", async (TenantContext t, BoDb db, HttpRequest request, bool? dryRun, bool? platform, CancellationToken ct) =>
        {
            t.Require("i18n.import");
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var lines = ParseCsv(await reader.ReadToEndAsync(ct));
            if (lines.Count < 1 || !lines[0].SequenceEqual(["entity_type", "entity_id", "field", "lang", "source_text", "text"]))
            {
                throw BoProblem.Invalid("BAD_CSV", "Expected header: entity_type,entity_id,field,lang,source_text,text");
            }
            var edits = lines.Skip(1).Where(l => l.Count == 6)
                .Select(l => new TranslationEdit(l[0], l[1], l[2], l[3], string.IsNullOrWhiteSpace(l[5]) ? null : l[5].Trim(), platform == true))
                .Where(e => e.Text is not null).ToList();
            return await db.TenantAsync(t, async (conn, tx) =>
            {
                var diff = new List<object>();
                var changed = new List<TranslationEdit>();
                foreach (var e in edits)
                {
                    var current = await conn.ExecuteScalarAsync<string?>("""
                        SELECT text FROM bo.translation WHERE operator_id IS NOT DISTINCT FROM @op AND brand_id IS NULL
                          AND entity_type = @EntityType AND entity_id = @EntityId AND field = @Field AND lang = @Lang
                        """, new { op = e.Platform ? null : t.OperatorId, e.EntityType, e.EntityId, e.Field, e.Lang }, tx);
                    if (current == e.Text)
                    {
                        continue;
                    }
                    changed.Add(e);
                    diff.Add(new { e.EntityType, e.EntityId, e.Field, e.Lang, before = current, after = e.Text, kind = current is null ? "new" : "changed" });
                }
                if (dryRun != false)
                {
                    // Validation errors surface in the dry run too.
                    var errors = changed.Select(e => Validate(e)).Where(x => x is not null).ToList();
                    return Results.Ok(new { dryRun = true, rows = edits.Count, changes = diff, errors });
                }
                var saved = changed.Count == 0 ? 0 : await SaveAsync(conn, tx, t, changed, "import");
                return Results.Ok(new { dryRun = false, rows = edits.Count, saved });
            }, ct);
        });

        return api;
    }

    private static async Task<int> SaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantContext t, IReadOnlyList<TranslationEdit> items, string source)
    {
        var operatorLanguages = t.OperatorId is { } op
            ? await conn.ExecuteScalarAsync<string[]>("SELECT languages FROM bo.operator WHERE id = @op", new { op }, tx) ?? []
            : null;
        var all = (await conn.QueryAsync<string>("SELECT code FROM bo.language", transaction: tx)).ToHashSet();
        foreach (var e in items)
        {
            if (Validate(e) is { } error)
            {
                throw BoProblem.Invalid("INVALID_TRANSLATION", error);
            }
            if (e.Platform)
            {
                t.RequirePlatform();
            }
            else
            {
                t.RequireOperator();
            }
            if (!all.Contains(e.Lang) || (!e.Platform && operatorLanguages is not null && !operatorLanguages.Contains(e.Lang)))
            {
                throw BoProblem.Invalid("BAD_LANGUAGE", $"Language '{e.Lang}' is not enabled for this operator");
            }
            if (e.Field == "template" && e.Text is not null)
            {
                var sourceTemplate = await SourceTemplateAsync(conn, tx, e.EntityType, e.EntityId)
                    ?? throw BoProblem.Invalid("UNKNOWN_ENTITY", $"{e.EntityType} {e.EntityId} does not exist");
                if (LintTemplate(sourceTemplate, e.Text) is { } lint)
                {
                    throw BoProblem.Invalid("TEMPLATE_LINT", $"{e.EntityType} {e.EntityId} ({e.Lang}): {lint}");
                }
            }
            else if (!await EntityExistsAsync(conn, tx, e.EntityType, e.EntityId))
            {
                throw BoProblem.Invalid("UNKNOWN_ENTITY", $"{e.EntityType} {e.EntityId} does not exist");
            }
            long? operatorId = e.Platform ? null : t.OperatorId;
            if (e.Text is null)
            {
                await conn.ExecuteAsync("""
                    DELETE FROM bo.translation WHERE operator_id IS NOT DISTINCT FROM @operatorId AND brand_id IS NULL
                      AND entity_type = @EntityType AND entity_id = @EntityId AND field = @Field AND lang = @Lang
                    """, new { operatorId, e.EntityType, e.EntityId, e.Field, e.Lang }, tx);
            }
            else
            {
                await conn.ExecuteAsync("""
                    INSERT INTO bo.translation (operator_id, entity_type, entity_id, field, lang, text, source, updated_by)
                    VALUES (@operatorId, @EntityType, @EntityId, @Field, @Lang, @Text, @source, @user)
                    ON CONFLICT (operator_id, brand_id, entity_type, entity_id, field, lang) DO UPDATE SET
                      text = EXCLUDED.text, source = EXCLUDED.source, updated_by = EXCLUDED.updated_by, updated_at = now(),
                      version = bo.translation.version + 1
                    """, new { operatorId, e.EntityType, e.EntityId, e.Field, e.Lang, Text = e.Text.Trim(), source, user = t.UserId }, tx);
            }
        }
        await Audit.WriteAsync(conn, tx, t, source == "import" ? "i18n.imported" : "i18n.saved", "translation",
            $"{items[0].EntityType}:{items.Count}", after: items.Take(50));
        await Audit.OutboxAsync(conn, tx, t.OperatorId, $"bo.changed.{t.OperatorId?.ToString() ?? "platform"}.i18n",
            new { entities = items.Select(i => new { i.EntityType, i.EntityId }).Distinct().Take(500) });
        return items.Count;
    }

    private static string? Validate(TranslationEdit e)
    {
        if (!EntityTypes.Contains(e.EntityType))
        {
            return $"Unknown entity type '{e.EntityType}'";
        }
        var allowed = e.EntityType is "market_type" or "outcome_type" ? new[] { "template" }
            : e.EntityType == "competitor" ? ["name", "short_name", "abbreviation"] : ["name"];
        if (!allowed.Contains(e.Field))
        {
            return $"{e.EntityType} has no field '{e.Field}' (allowed: {string.Join(", ", allowed)})";
        }
        if (e.Text is { Length: > 500 })
        {
            return "Translations are at most 500 characters";
        }
        return e.Field switch
        {
            "short_name" when e.Text is { Length: > 12 } => "Short names are at most 12 characters (mobile)",
            "abbreviation" when e.Text is { Length: > 4 } => "Abbreviations are at most 4 characters",
            _ => null,
        };
    }

    /// <summary>The translation must use exactly the placeholders of the source template (order is free).</summary>
    public static string? LintTemplate(string source, string translation)
    {
        if (translation.Count(c => c == '{') != translation.Count(c => c == '}'))
        {
            return "Unbalanced braces";
        }
        var expected = Placeholder().Matches(source).Select(m => m.Value).Order(StringComparer.Ordinal).ToList();
        var actual = Placeholder().Matches(translation).Select(m => m.Value).Order(StringComparer.Ordinal).ToList();
        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            return string.Join("; ", new[]
            {
                missing.Count > 0 ? $"missing {string.Join(" ", missing)}" : null,
                extra.Count > 0 ? $"unknown {string.Join(" ", extra)}" : null,
            }.OfType<string>());
        }
        return null;
    }

    private static string DefaultField(string entityType) => entityType is "market_type" or "outcome_type" ? "template" : "name";

    public static string OutcomeKey(int marketTypeId, string variant, string code) => $"{marketTypeId}:{variant}:{code}";

    private sealed class SourceRow
    {
        public string EntityId { get; init; } = "";
        public string? ProviderI18n { get; init; }
        public string? Context { get; init; }
        public int Total { get; init; }
    }

    /// <summary>Feed entities of a type with their provider texts (and, for "missing", only those lacking a language).</summary>
    private static async Task<IReadOnlyList<SourceRow>> SourcesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string entityType, string field,
        string? q, string[]? missingLangs, long? operatorId, int offset, int take)
    {
        var (from, idExpr, i18nExpr, context) = entityType switch
        {
            "sport" => ("sb.sport x", "x.id::text", "x.name_i18n", "NULL"),
            "category" => ("sb.category x JOIN sb.sport s ON s.id = x.sport_id", "x.id::text", "x.name_i18n", "s.name_i18n->>'en'"),
            "tournament" => ("sb.tournament x JOIN sb.category c ON c.id = x.category_id JOIN sb.sport s ON s.id = x.sport_id",
                "x.id::text", "x.name_i18n", "concat_ws(' / ', s.name_i18n->>'en', c.name_i18n->>'en')"),
            "competitor" => ("sb.competitor x LEFT JOIN sb.sport s ON s.id = x.sport_id", "x.id::text", "x.name_i18n",
                "concat_ws(' · ', s.name_i18n->>'en', x.country_code)"),
            "market_type" => ("sb.market_description x", "x.id::text", "x.name_template_i18n", "x.code"),
            _ => ("sb.market_description_outcome x JOIN sb.market_description md ON md.id = x.market_description_id",
                "x.market_description_id || ':' || x.variant || ':' || x.code", "x.name_template_i18n",
                "concat_ws(' · ', md.name_template_i18n->>'en', 'outcome ' || x.code)"),
        };
        var sql = $"""
            SELECT r.*, count(*) OVER ()::int AS total FROM (
              SELECT {idExpr} AS entityid, {i18nExpr}::text AS provideri18n, {context} AS context
              FROM {from}
            ) r
            WHERE (@q::text IS NULL OR r.provideri18n ILIKE '%' || @q || '%' OR r.entityid = @q OR EXISTS (
                     SELECT 1 FROM bo.translation tr WHERE tr.entity_type = @entityType AND tr.entity_id = r.entityid AND tr.text ILIKE '%' || @q || '%'))
              AND (@missing::text[] IS NULL OR EXISTS (
                     SELECT 1 FROM unnest(@missing::text[]) l(lang)
                     WHERE NOT jsonb_exists(r.provideri18n::jsonb, l.lang) AND NOT EXISTS (
                       SELECT 1 FROM bo.translation tr WHERE tr.entity_type = @entityType AND tr.entity_id = r.entityid
                         AND tr.field = @field AND tr.lang = l.lang AND tr.brand_id IS NULL
                         AND (tr.operator_id IS NULL OR tr.operator_id = @operatorId))))
            ORDER BY r.context NULLS FIRST, r.provideri18n
            LIMIT @take OFFSET @offset
            """;
        return (await conn.QueryAsync<SourceRow>(sql, new { q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(), missing = missingLangs, entityType, field, operatorId, take, offset }, tx)).ToList();
    }

    private static Task<string?> SourceTemplateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string entityType, string entityId)
    {
        if (entityType == "market_type" && int.TryParse(entityId, out var md))
        {
            return conn.ExecuteScalarAsync<string?>("SELECT name_template_i18n->>'en' FROM sb.market_description WHERE id = @md", new { md }, tx);
        }
        var parts = entityId.Split(':', 3);
        if (entityType == "outcome_type" && parts.Length == 3 && int.TryParse(parts[0], out var mdId))
        {
            return conn.ExecuteScalarAsync<string?>("""
                SELECT name_template_i18n->>'en' FROM sb.market_description_outcome WHERE market_description_id = @mdId AND variant = @v AND code = @c
                """, new { mdId, v = parts[1], c = parts[2] }, tx);
        }
        return Task.FromResult<string?>(null);
    }

    private static Task<bool> EntityExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string entityType, string entityId) =>
        long.TryParse(entityId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && entityType is "sport" or "category" or "tournament" or "competitor"
            ? conn.ExecuteScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM sb.{entityType} WHERE id = @id)", new { id }, tx)
            : Task.FromResult(false);

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    /// <summary>RFC 4180 CSV (quoted fields, doubled quotes, newlines inside quotes); a leading BOM is ignored.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var start = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
