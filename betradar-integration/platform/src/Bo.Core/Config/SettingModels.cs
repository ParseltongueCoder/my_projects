using System.Text.Json.Nodes;

namespace Bo.Core.Config;

/// <summary>Scope levels from most general to most specific (docs/09 §2.1). The numeric value is the depth.</summary>
public enum ScopeType
{
    Platform = 0,
    Operator = 1,
    Brand = 2,
    Sport = 3,
    Category = 4,
    Tournament = 5,
    Event = 6,
    Market = 7,
}

public enum ValueType
{
    Bool,
    Int,
    Decimal,
    String,
    Enum,
    Money,
    Json,
    StringList,
}

public enum Combine
{
    Override,
    AllPath,
    MinPath,
    MaxPath,
}

public sealed record SettingDef(
    string Key,
    string Module,
    ValueType Type,
    IReadOnlyList<ScopeType> AllowedScopes,
    JsonNode? Default,
    string Description,
    Combine Combine = Combine.Override,
    bool AllowsMarketType = false,
    IReadOnlyList<string>? EnumValues = null,
    decimal? Min = null,
    decimal? Max = null,
    bool OperatorEditable = true,
    bool RequiresApproval = false,
    string CustomerCombine = "none");

/// <summary>One stored value: key K has value V at scope (type, id) for an operator (NULL = platform).</summary>
public sealed record SettingRow(
    long Id,
    long? OperatorId,
    ScopeType ScopeType,
    long? ScopeId,
    int? MarketTypeId,
    string Key,
    JsonNode Value,
    long ChangeSetId,
    DateTime UpdatedAt);

/// <summary>Where in the hierarchy a value is asked for. Unknown levels stay null.</summary>
public sealed record ScopeContext(
    long OperatorId,
    long? BrandId = null,
    long? SportId = null,
    long? CategoryId = null,
    long? TournamentId = null,
    long? EventId = null,
    long? MarketId = null,
    int? MarketTypeId = null)
{
    public long? IdAt(ScopeType level) => level switch
    {
        ScopeType.Brand => BrandId,
        ScopeType.Sport => SportId,
        ScopeType.Category => CategoryId,
        ScopeType.Tournament => TournamentId,
        ScopeType.Event => EventId,
        ScopeType.Market => MarketId,
        _ => null,
    };
}

public sealed record TraceEntry(SettingRow Row, int Specificity, bool Winner);

/// <summary>The effective value of a key plus why: every candidate row on the path, most specific first.</summary>
public sealed record EffectiveSetting(string Key, JsonNode? Value, bool IsDefault, SettingRow? Winner, IReadOnlyList<TraceEntry> Trace);

public static class JsonNumbers
{
    /// <summary>Reads a JSON number whether the node was parsed or built in memory (e.g. catalog defaults).</summary>
    public static decimal Decimal(JsonNode node) =>
        decimal.Parse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
}

public static class ScopeTypes
{
    public static string ToDb(this ScopeType s) => s.ToString().ToLowerInvariant();

    public static ScopeType Parse(string s) =>
        Enum.TryParse<ScopeType>(s, ignoreCase: true, out var v) && Enum.IsDefined(v)
            ? v
            : throw new ArgumentException($"Unknown scope type '{s}'");

    public static bool TryParse(string? s, out ScopeType value) =>
        Enum.TryParse(s, ignoreCase: true, out value) && Enum.IsDefined(value) && !int.TryParse(s, out _);
}
