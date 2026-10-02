namespace UofSim.Core.Producers;

/// <param name="UrlCode">Path segment of the producer's REST API, e.g. <c>liveodds</c> → <c>/v1/liveodds/</c>.</param>
public sealed record ProducerDefinition(
    int Id,
    string Name,
    string Description,
    string UrlCode,
    string Scope,
    int StatefulRecoveryWindowMinutes);

public static class Producers
{
    public static readonly ProducerDefinition LiveOdds = new(1, "LO", "Live Odds", "liveodds", "live", 600);
    public static readonly ProducerDefinition Ctrl = new(3, "Ctrl", "Betradar Ctrl", "pre", "prematch", 4320);

    public static readonly IReadOnlyList<ProducerDefinition> All = [LiveOdds, Ctrl];

    public static ProducerDefinition? ByUrlCode(string urlCode) =>
        All.FirstOrDefault(p => string.Equals(p.UrlCode, urlCode, StringComparison.OrdinalIgnoreCase));

    public static ProducerDefinition? ById(int id) => All.FirstOrDefault(p => p.Id == id);
}
