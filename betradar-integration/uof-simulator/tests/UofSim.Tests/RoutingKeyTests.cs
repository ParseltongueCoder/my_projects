using UofSim.Core.Routing;

namespace UofSim.Tests;

public class RoutingKeyTests
{
    [Fact]
    public void Live_odds_change_has_all_eight_segments()
    {
        var key = RoutingKey.Build(MessageTypes.OddsChange, Priority.Hi, Interest.LiveOnly, 1, "sr:match:900000001");
        Assert.Equal("hi.-.live.odds_change.1.sr:match.900000001.-", key);
    }

    [Fact]
    public void Prematch_odds_change_uses_pre_segment()
    {
        var key = RoutingKey.Build(MessageTypes.OddsChange, Priority.Lo, Interest.PrematchOnly, 1, "sr:match:42");
        Assert.Equal("lo.pre.-.odds_change.1.sr:match.42.-", key);
    }

    [Fact]
    public void System_messages_carry_no_event_data()
    {
        Assert.Equal("-.-.-.alive.-.-.-.-", RoutingKey.System(MessageTypes.Alive));
        Assert.Equal("-.-.-.snapshot_complete.-.-.-.7", RoutingKey.System(MessageTypes.SnapshotComplete, nodeId: 7));
    }

    [Fact]
    public void Urn_type_may_contain_colons()
    {
        Assert.Equal(("vf:match", "123"), RoutingKey.SplitUrn("vf:match:123"));
        Assert.Equal(8, RoutingKey.Build(MessageTypes.BetStop, eventUrn: "sr:stage:5").Split('.').Length);
    }

    [Theory]
    [InlineData("sr:match:")]
    [InlineData("123")]
    [InlineData(":123")]
    public void Invalid_urn_is_rejected(string urn) =>
        Assert.Throws<ArgumentException>(() => RoutingKey.SplitUrn(urn));
}
