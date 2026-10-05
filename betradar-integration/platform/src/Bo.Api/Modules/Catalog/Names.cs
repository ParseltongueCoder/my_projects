using System.Text.Json;
using System.Text.Json.Nodes;
using Bo.Api.Modules.Config;
using Bo.Core.Config;
using Dapper;
using Npgsql;

namespace Bo.Api.Modules.Catalog;

/// <summary>A resolved display text and where it came from (operator / platform / provider).</summary>
public sealed record ResolvedName(string Text, string Source, string Lang);

/// <summary>
/// Display names with the fallback chain of docs/06 §3.4: for the requested language, then each fallback language
/// (CFG <c>i18n.fallback_langs</c>): operator translation → platform translation → provider name (<c>sb.*.name_i18n</c>);
/// finally the provider's English or any name.
/// </summary>
public sealed class Names(SettingsSnapshotCache settings)
{
    public async Task<IReadOnlyList<string>> LanguageChainAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long operatorId, string lang)
    {
        var rows = await settings.GetAsync(conn, tx, operatorId);
        var fallback = SettingResolver.Resolve(SettingCatalog.ByKey["i18n.fallback_langs"], rows, new ScopeContext(operatorId)).Value;
        var chain = new List<string> { lang };
        if (fallback is JsonArray list)
        {
            chain.AddRange(list.Select(v => v!.GetValue<string>()).Where(l => !chain.Contains(l)));
        }
        return chain;
    }

    /// <summary>Resolves one field for many entities. <paramref name="provider"/> maps id → provider name_i18n JSON.</summary>
    public async Task<Dictionary<string, ResolvedName>> ResolveAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string entityType, IReadOnlyDictionary<string, string?> provider,
        IReadOnlyList<string> languages, string field = "name")
    {
        var ids = provider.Keys.ToArray();
        // RLS returns this operator's rows and platform rows only.
        var translations = (await conn.QueryAsync<(long? OperatorId, string EntityId, string Lang, string Text)>("""
            SELECT operator_id, entity_id, lang, text FROM bo.translation
            WHERE entity_type = @entityType AND field = @field AND entity_id = ANY(@ids) AND brand_id IS NULL
            """, new { entityType, field, ids }, tx))
            .ToLookup(t => t.EntityId);

        var result = new Dictionary<string, ResolvedName>(ids.Length);
        foreach (var id in ids)
        {
            var own = translations[id].ToList();
            var providerNames = Parse(provider[id]);
            result[id] = Pick(own, providerNames, languages) ?? new ResolvedName(id, "id", languages[0]);
        }
        return result;
    }

    private static ResolvedName? Pick(List<(long? OperatorId, string EntityId, string Lang, string Text)> own,
        Dictionary<string, string> providerNames, IReadOnlyList<string> languages)
    {
        foreach (var lang in languages)
        {
            if (own.FirstOrDefault(t => t.OperatorId is not null && t.Lang == lang) is { Text: { } op })
            {
                return new ResolvedName(op, "operator", lang);
            }
            if (own.FirstOrDefault(t => t.OperatorId is null && t.Lang == lang) is { Text: { } platform })
            {
                return new ResolvedName(platform, "platform", lang);
            }
            if (providerNames.TryGetValue(lang, out var p))
            {
                return new ResolvedName(p, "provider", lang);
            }
        }
        if (providerNames.TryGetValue("en", out var en))
        {
            return new ResolvedName(en, "provider", "en");
        }
        return providerNames.Count > 0 ? new ResolvedName(providerNames.First().Value, "provider", providerNames.First().Key) : null;
    }

    public static Dictionary<string, string> Parse(string? nameI18n)
    {
        if (string.IsNullOrEmpty(nameI18n))
        {
            return [];
        }
        using var doc = JsonDocument.Parse(nameI18n);
        return doc.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 })
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }
}
