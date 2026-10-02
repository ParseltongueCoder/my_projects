using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UofSim.Core.Scenarios;

/// <summary>
/// A match script in YAML: which event, how strong the teams are, and timed actions.
/// See <c>data/scenarios/*.yaml</c> and docs/03-feed-simulator.md §4.
/// </summary>
public sealed class Scenario
{
    public string Name { get; set; } = "";
    public string Event { get; set; } = "";

    /// <summary>Expected goals over a full match; drives the odds model.</summary>
    public StrengthDef Strength { get; set; } = new();

    /// <summary>Bookmaker margin applied to fair odds.</summary>
    public double Margin { get; set; } = 0.05;

    public List<ScenarioStep> Steps { get; set; } = [];

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static Scenario Parse(string yaml)
    {
        var scenario = Yaml.Deserialize<Scenario>(yaml)
            ?? throw new InvalidDataException("Empty scenario");
        if (string.IsNullOrWhiteSpace(scenario.Event))
        {
            throw new InvalidDataException("Scenario needs an 'event' (e.g. sr:match:900000002)");
        }
        foreach (var step in scenario.Steps)
        {
            _ = step.OffsetMs; // validates 'at'
            if (!ScenarioActions.All.Contains(step.Action))
            {
                throw new InvalidDataException(
                    $"Unknown action '{step.Action}'. Known: {string.Join(", ", ScenarioActions.All)}");
            }
        }
        return scenario;
    }

    public static Scenario Load(string path) => Parse(File.ReadAllText(path));
}

public sealed class StrengthDef
{
    public double Home { get; set; } = 1.4;
    public double Away { get; set; } = 1.1;
}

public sealed class ScenarioStep
{
    /// <summary>Time from scenario start: <c>1500ms</c>, <c>15s</c>, <c>2m</c>.</summary>
    public string At { get; set; } = "0s";
    public string Action { get; set; } = "";

    /// <summary>Match minute shown in the clock and used by the odds model.</summary>
    public int? Minute { get; set; }

    /// <summary><c>home</c> or <c>away</c> (goal).</summary>
    public string? Team { get; set; }

    /// <summary>bet_stop groups, default <c>all</c>.</summary>
    public string? Groups { get; set; }

    /// <summary>settle: 1 = live scouted, 2 = confirmed.</summary>
    public int? Certainty { get; set; }

    /// <summary>cancel: void reason id.</summary>
    public int? VoidReason { get; set; }

    [YamlIgnore]
    public long OffsetMs => ParseDuration(At);

    internal static long ParseDuration(string value)
    {
        var v = value.Trim();
        (string Suffix, double Factor)[] units = [("ms", 1), ("s", 1000), ("m", 60_000)];
        foreach (var (suffix, factor) in units)
        {
            if (v.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(v[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && n >= 0)
            {
                return (long)(n * factor);
            }
        }
        throw new InvalidDataException($"Invalid time '{value}' (use e.g. 500ms, 15s, 2m)");
    }
}

public static class ScenarioActions
{
    public const string PrematchOdds = "prematch_odds";
    public const string Kickoff = "kickoff";
    public const string OddsUpdate = "odds_update";
    public const string BetStop = "bet_stop";
    public const string Goal = "goal";
    public const string Halftime = "halftime";
    public const string SecondHalf = "second_half";
    public const string FullTime = "full_time";
    public const string Settle = "settle";
    public const string RollbackSettlement = "rollback_settlement";
    public const string Cancel = "cancel";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        PrematchOdds, Kickoff, OddsUpdate, BetStop, Goal, Halftime, SecondHalf, FullTime, Settle, RollbackSettlement, Cancel,
    };
}
