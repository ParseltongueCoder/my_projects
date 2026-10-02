using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UofSim.Core.Catalog;

/// <summary>
/// Synthetic sports data the mock Sports API serves (sports → categories → tournaments, competitors, events).
/// Loaded from <c>data/catalog.yaml</c>; all ids and names are made up.
/// </summary>
public sealed class SimCatalog
{
    public List<SportDef> Sports { get; set; } = [];
    public List<CompetitorDef> Competitors { get; set; } = [];
    public List<EventDef> Events { get; set; } = [];

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static SimCatalog Load(string path)
    {
        var catalog = Yaml.Deserialize<SimCatalog>(File.ReadAllText(path)) ?? new SimCatalog();
        catalog.Validate();
        return catalog;
    }

    public IEnumerable<(SportDef Sport, CategoryDef Category, TournamentDef Tournament)> Tournaments =>
        from s in Sports
        from c in s.Categories
        from t in c.Tournaments
        select (s, c, t);

    public (SportDef Sport, CategoryDef Category, TournamentDef Tournament)? FindTournament(string urn)
    {
        foreach (var entry in Tournaments)
        {
            if (entry.Tournament.Id == urn)
            {
                return entry;
            }
        }
        return null;
    }

    public CompetitorDef? FindCompetitor(string urn) => Competitors.FirstOrDefault(c => c.Id == urn);

    public EventDef? FindEvent(string urn) => Events.FirstOrDefault(e => e.Id == urn);

    private void Validate()
    {
        foreach (var e in Events)
        {
            if (FindTournament(e.Tournament) is null)
            {
                throw new InvalidDataException($"Event {e.Id}: unknown tournament {e.Tournament}");
            }
            foreach (var c in new[] { e.Home, e.Away })
            {
                if (FindCompetitor(c) is null)
                {
                    throw new InvalidDataException($"Event {e.Id}: unknown competitor {c}");
                }
            }
        }
    }
}

public sealed class SportDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<CategoryDef> Categories { get; set; } = [];

    /// <summary>Numeric part of the URN, used in routing keys (<c>sr:sport:1</c> → 1).</summary>
    public int NumericId => int.Parse(Id[(Id.LastIndexOf(':') + 1)..]);
}

public sealed class CategoryDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CountryCode { get; set; }
    public List<TournamentDef> Tournaments { get; set; } = [];
}

public sealed class TournamentDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SeasonDef? Season { get; set; }
}

public sealed class SeasonDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>yyyy-MM-dd</summary>
    public string StartDate { get; set; } = "";
    /// <summary>yyyy-MM-dd</summary>
    public string EndDate { get; set; } = "";
    public string? Year { get; set; }
}

public sealed class CompetitorDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Abbreviation { get; set; } = "";
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
}

public sealed class EventDef
{
    public string Id { get; set; } = "";
    public string Tournament { get; set; } = "";
    public string Home { get; set; } = "";
    public string Away { get; set; } = "";

    /// <summary>Kick-off relative to simulator start, so schedules always contain "today's" matches.</summary>
    public int ScheduledOffsetMinutes { get; set; }
}
