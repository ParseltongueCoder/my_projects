namespace Platform.Canonical.Feed;

public sealed record CompetitorRef(string Urn, string Name, string? Abbreviation, string? CountryCode, string? Qualifier);

/// <summary>Everything needed to place an event in the canonical hierarchy (resolved from the provider's API).</summary>
public sealed record EventRef(
    string EventUrn,
    string SportUrn,
    string SportName,
    string? CategoryUrn,
    string? CategoryName,
    string? CategoryCountryCode,
    string? TournamentUrn,
    string? TournamentName,
    DateTimeOffset? Scheduled,
    IReadOnlyList<CompetitorRef> Competitors);

public sealed record SpecifierRef(string Name, string Type);

public sealed record OutcomeDescriptionRef(string Code, string NameTemplate);

/// <summary>A provider market type (e.g. UOF market 18 "Total") with its name templates.</summary>
public sealed record MarketDescriptionRef(
    int Id,
    string NameTemplate,
    IReadOnlyList<string> Groups,
    IReadOnlyList<SpecifierRef> Specifiers,
    IReadOnlyList<OutcomeDescriptionRef> Outcomes);
