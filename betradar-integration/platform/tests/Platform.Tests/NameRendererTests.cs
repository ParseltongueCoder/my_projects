using Platform.Canonical.Feed;

namespace Platform.Tests;

public class NameRendererTests
{
    private static readonly string[] Teams = ["Tbilisi Lions", "Batumi Waves"];

    [Theory]
    [InlineData("{$competitor1}", "", "Tbilisi Lions")]
    [InlineData("draw or {$competitor2}", "", "draw or Batumi Waves")]
    [InlineData("over {total}", "total=2.5", "over 2.5")]
    [InlineData("{$competitor1} ({+hcp})", "hcp=-1.5", "Tbilisi Lions (-1.5)")]
    [InlineData("{$competitor2} ({-hcp})", "hcp=-1.5", "Batumi Waves (+1.5)")]
    [InlineData("{!periodnr} half - total {total}", "periodnr=1|total=1.5", "1st half - total 1.5")]
    [InlineData("{$event}", "", "Tbilisi Lions v Batumi Waves")]
    [InlineData("Handicap {hcp}", "", "Handicap {hcp}")]
    public void Templates_are_rendered(string template, string specifiers, string expected) =>
        Assert.Equal(expected, NameRenderer.Render(template, specifiers, Teams));
}
