using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bo.Core.Config;

/// <summary>Validates one change against the catalog. Returns null when the change is allowed, else an error message.</summary>
public static partial class SettingValidator
{
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();

    public static string? Validate(SettingDef def, ScopeType scope, int? marketTypeId, JsonNode? value, bool platformUser, bool isDelete)
    {
        if (!def.AllowedScopes.Contains(scope))
        {
            return $"'{def.Key}' cannot be set at {scope.ToDb()} level (allowed: {string.Join(", ", def.AllowedScopes.Select(s => s.ToDb()))})";
        }
        if (marketTypeId is not null && !def.AllowsMarketType)
        {
            return $"'{def.Key}' does not take a market type qualifier";
        }
        if (scope == ScopeType.Platform && !platformUser)
        {
            return "Only platform staff can change platform-wide settings";
        }
        if (!def.OperatorEditable && !platformUser)
        {
            return $"'{def.Key}' is managed by the platform";
        }
        return isDelete ? null : ValidateValue(def, value);
    }

    public static string? ValidateValue(SettingDef def, JsonNode? value)
    {
        if (value is null)
        {
            return "A value is required";
        }
        var kind = value.GetValueKind();
        switch (def.Type)
        {
            case ValueType.Bool:
                return kind is JsonValueKind.True or JsonValueKind.False ? null : "Expected true or false";
            case ValueType.Int:
                if (kind != JsonValueKind.Number || JsonNumbers.Decimal(value) is var i && i != decimal.Truncate(i))
                {
                    return "Expected a whole number";
                }
                return Range(def, i);
            case ValueType.Decimal:
                if (kind != JsonValueKind.Number)
                {
                    return "Expected a number";
                }
                return Range(def, JsonNumbers.Decimal(value));
            case ValueType.String:
                return kind == JsonValueKind.String && value.GetValue<string>().Length is > 0 and <= 200 ? null : "Expected a text value";
            case ValueType.Enum:
                return kind == JsonValueKind.String && def.EnumValues!.Contains(value.GetValue<string>())
                    ? null
                    : $"Expected one of: {string.Join(", ", def.EnumValues!)}";
            case ValueType.StringList:
                return kind == JsonValueKind.Array && value.AsArray().All(v => v?.GetValueKind() == JsonValueKind.String)
                    ? null
                    : "Expected a list of texts";
            case ValueType.Money:
                if (kind != JsonValueKind.Object || value.AsObject().Count == 0)
                {
                    return "Expected amounts per currency, e.g. {\"GEL\": 500}";
                }
                foreach (var (currency, amount) in value.AsObject())
                {
                    if (!CurrencyCode().IsMatch(currency))
                    {
                        return $"'{currency}' is not a currency code";
                    }
                    if (amount?.GetValueKind() != JsonValueKind.Number)
                    {
                        return $"Amount for {currency} must be a number";
                    }
                    if (Range(def, JsonNumbers.Decimal(amount)) is { } error)
                    {
                        return $"{currency}: {error}";
                    }
                }
                return null;
            default:
                if (def.Key == "odds.override_max_ttl_min")
                {
                    return kind == JsonValueKind.Object && value.AsObject().Count == 2
                           && new[] { "live", "prematch" }.All(p => value[p] is { } m && m.GetValueKind() == JsonValueKind.Number
                                                                 && JsonNumbers.Decimal(m) is >= 1 and <= 10080 && JsonNumbers.Decimal(m) % 1 == 0)
                        ? null
                        : "Expected {\"live\": minutes, \"prematch\": minutes} (1-10080)";
                }
                return kind is JsonValueKind.Object or JsonValueKind.Array ? null : "Expected a JSON object";
        }
    }

    private static string? Range(SettingDef def, decimal v) =>
        def.Min is { } min && v < min ? $"Must be at least {min}"
        : def.Max is { } max && v > max ? $"Must be at most {max}"
        : null;
}
