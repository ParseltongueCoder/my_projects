using Offer.Core;

namespace Offer.Tests;

public sealed class MarginTests
{
    [Theory]
    [InlineData(1.005, "std", null)]
    [InlineData(1.019, "std", 1.01)]
    [InlineData(1.999, "std", 1.99)]
    [InlineData(2.069, "std", 2.06)]
    [InlineData(3.317, "std", 3.30)]
    [InlineData(5.55, "std", 5.5)]
    [InlineData(7.39, "std", 7.2)]
    [InlineData(19.9, "std", 19.5)]
    [InlineData(66.0, "std", 65.0)]
    [InlineData(1001.0, "std", 1000.0)]
    [InlineData(3.317, "fine", 3.30)]
    [InlineData(4.07, "fine", 4.06)]
    [InlineData(3.317, "none", 3.31)]
    public void Ladder_rounds_down_to_the_step_of_the_band(double odds, string ladder, double? expected) =>
        Assert.Equal(expected is { } e ? (decimal)e : null, OddsLadder.Floor((decimal)odds, ladder));

    public static TheoryData<double[], string> Markets() => new()
    {
        { new[] { 2.10, 3.40, 3.60 }, "power" },
        { new[] { 2.10, 3.40, 3.60 }, "proportional" },
        { new[] { 2.10, 3.40, 3.60 }, "shin" },
        { new[] { 1.05, 9.0, 21.0 }, "power" },
        { new[] { 1.05, 9.0, 21.0 }, "shin" },
        { new[] { 1.90, 1.90 }, "power" },
    };

    [Theory]
    [MemberData(nameof(Markets))]
    public void Removal_gives_probabilities_and_application_hits_the_target_booksum(double[] odds, string method)
    {
        var fair = Margin.Remove(odds.Select(o => 1 / o).ToArray(), method);
        Assert.Equal(1, fair.Sum(), 9);
        Assert.All(fair, p => Assert.InRange(p, 0, 1));
        // Order is kept: the favourite stays the favourite.
        Assert.Equal(odds.Select((o, i) => i).OrderBy(i => odds[i]), fair.Select((p, i) => i).OrderByDescending(i => fair[i]));

        foreach (var margin in new[] { 0.0, 0.03, 0.07, 0.15 })
        {
            Assert.Equal(1 + margin, Margin.Apply(fair, margin, method).Sum(), 9);
        }
    }

    [Fact]
    public void Power_puts_more_margin_on_the_long_shot_than_proportional()
    {
        var fair = Margin.Remove([1 / 1.25, 1 / 4.5, 1 / 15.0], "proportional");
        var power = Margin.Apply(fair, 0.08, "power");
        var proportional = Margin.Apply(fair, 0.08, "proportional");
        Assert.True(power[0] / fair[0] < proportional[0] / fair[0]);  // favourite: smaller cut
        Assert.True(power[2] / fair[2] > proportional[2] / fair[2]);  // long shot: bigger cut
    }

    [Fact]
    public void Same_input_gives_the_same_offer()
    {
        var market = new MarketInput(1, "active", "active", [new("1", 2.10m), new("2", 3.40m), new("3", 3.60m)]);
        var settings = new PricingSettings(Mode: "target", Pct: 0.065m);
        var a = OfferPricer.Price(market, settings, new MarketGate(), [], DateTime.UtcNow);
        var b = OfferPricer.Price(market with { }, settings with { }, new MarketGate(), [], DateTime.UtcNow);
        Assert.Equal(a.Outcomes.Select(o => o.Odds), b.Outcomes.Select(o => o.Odds));
        Assert.True(a.OfferOverround >= 0.065m);  // the ladder only ever adds margin
    }
}
