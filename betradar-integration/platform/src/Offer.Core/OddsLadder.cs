namespace Offer.Core;

/// <summary>
/// Odds ladders (CFG <c>odds.ladder</c>, docs/06 §4.2). Prices are always rounded <b>down</b> to a ladder step:
/// we never offer more than the computed price. Steps start at multiples of themselves, so flooring to a step
/// within a band keeps the price on the ladder.
/// </summary>
public static class OddsLadder
{
    private sealed record Band(decimal From, decimal Step);

    // Each band applies from its lower bound up to the next band.
    private static readonly Band[] Std =
    [
        new(1.01m, 0.01m), new(2m, 0.02m), new(3m, 0.05m), new(4m, 0.1m), new(6m, 0.2m),
        new(10m, 0.5m), new(20m, 1m), new(50m, 5m), new(100m, 10m),
    ];

    private static readonly Band[] Fine =
    [
        new(1.01m, 0.01m), new(3m, 0.02m), new(5m, 0.05m), new(10m, 0.1m), new(20m, 0.5m), new(50m, 1m), new(100m, 5m),
    ];

    private static readonly Band[] None = [new(1.01m, 0.01m)];

    public static readonly IReadOnlyList<string> Codes = ["std", "fine", "none"];

    /// <summary>The highest ladder price that is not above <paramref name="odds"/>; below 1.01 there is no price (null).</summary>
    public static decimal? Floor(decimal odds, string ladder)
    {
        var bands = ladder switch
        {
            "fine" => Fine,
            "none" => None,
            _ => Std,
        };
        if (odds < bands[0].From)
        {
            return null;
        }
        var band = bands.Last(b => odds >= b.From);
        return decimal.Floor(odds / band.Step) * band.Step;
    }
}
