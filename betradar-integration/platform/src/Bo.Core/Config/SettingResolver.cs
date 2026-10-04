using System.Text.Json.Nodes;

namespace Bo.Core.Config;

/// <summary>
/// Resolves effective settings (docs/06 §5.3 with docs/09 §2.1-2.2):
/// candidates are the rows of the operator (and platform rows) on the context's path; specificity is
/// <c>depth × 2 + (market type qualifier ? 1 : 0)</c>, so a deeper level always wins and, at equal depth,
/// the market-type-qualified row wins. <see cref="Combine.AllPath"/> ANDs every value on the path
/// (hidden at a parent ⇒ hidden below), <see cref="Combine.MinPath"/> / <see cref="Combine.MaxPath"/> take the strictest.
/// Pure: the caller supplies the rows (a per-operator snapshot).
/// </summary>
public static class SettingResolver
{
    public static int Specificity(SettingRow row) => (int)row.ScopeType * 2 + (row.MarketTypeId is null ? 0 : 1);

    public static bool Matches(SettingRow row, ScopeContext ctx)
    {
        if (row.MarketTypeId is { } mt && mt != ctx.MarketTypeId)
        {
            return false;
        }
        return row.ScopeType switch
        {
            ScopeType.Platform => row.OperatorId is null,
            ScopeType.Operator => row.OperatorId == ctx.OperatorId,
            _ => row.OperatorId == ctx.OperatorId && ctx.IdAt(row.ScopeType) is { } id && id == row.ScopeId,
        };
    }

    public static EffectiveSetting Resolve(SettingDef def, IEnumerable<SettingRow> rows, ScopeContext ctx)
    {
        var candidates = rows
            .Where(r => r.Key == def.Key && Matches(r, ctx))
            .OrderByDescending(Specificity)
            .ThenByDescending(r => r.UpdatedAt)
            .ToList();

        if (candidates.Count == 0)
        {
            return new EffectiveSetting(def.Key, def.Default?.DeepClone(), true, null, []);
        }

        JsonNode? value;
        SettingRow? winner;
        switch (def.Combine)
        {
            case Combine.AllPath:
                // false anywhere on the path wins; the most specific false row explains it.
                winner = candidates.FirstOrDefault(r => r.Value.GetValueKind() == System.Text.Json.JsonValueKind.False) ?? candidates[0];
                value = JsonValue.Create(candidates.All(r => r.Value.GetValue<bool>()));
                break;
            case Combine.MinPath:
            case Combine.MaxPath:
                (value, winner) = Extreme(def, candidates, def.Combine == Combine.MinPath);
                break;
            default:
                winner = candidates[0];
                value = winner.Value.DeepClone();
                break;
        }

        var trace = candidates.Select(r => new TraceEntry(r, Specificity(r), ReferenceEquals(r, winner))).ToList();
        return new EffectiveSetting(def.Key, value, false, winner, trace);
    }

    public static IReadOnlyList<EffectiveSetting> ResolveAll(
        IEnumerable<SettingDef> defs, IReadOnlyCollection<SettingRow> rows, ScopeContext ctx) =>
        defs.Select(d => Resolve(d, rows, ctx)).ToList();

    private static (JsonNode? Value, SettingRow Winner) Extreme(SettingDef def, List<SettingRow> rows, bool min)
    {
        if (def.Type == ValueType.Money)
        {
            // Per currency: the strictest amount found on the path for each currency.
            var result = new JsonObject();
            SettingRow? winner = null;
            decimal? best = null;
            foreach (var row in rows)
            {
                foreach (var (currency, amount) in row.Value.AsObject())
                {
                    var a = JsonNumbers.Decimal(amount!);
                    if (result[currency] is not { } current || (min ? a < JsonNumbers.Decimal(current) : a > JsonNumbers.Decimal(current)))
                    {
                        result[currency] = a;
                    }
                    if (best is null || (min ? a < best : a > best))
                    {
                        best = a;
                        winner = row;
                    }
                }
            }
            return (result, winner ?? rows[0]);
        }
        var pick = min ? rows.MinBy(r => JsonNumbers.Decimal(r.Value))! : rows.MaxBy(r => JsonNumbers.Decimal(r.Value))!;
        return (pick.Value.DeepClone(), pick);
    }
}
