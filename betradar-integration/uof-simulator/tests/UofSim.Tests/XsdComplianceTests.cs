using System.Text;
using UofSim.Core.Messages;
using UofSim.Core.Recordings;

namespace UofSim.Tests;

/// <summary>
/// Checks our generated messages and description files against the official schemas.
/// Set UOF_XSD_DIR to a local folder holding UnifiedFeed.xsd, UnifiedFeedDescriptions.xsd and
/// UnifiedFeedResponse.xsd (e.g. from a local SDK checkout). The XSDs are not part of this repo.
/// </summary>
public class XsdComplianceTests
{
    private static readonly string DescriptionsDir =
        Path.Combine(AppContext.BaseDirectory, "data", "static", "descriptions");

    [XsdFact]
    public void Demo_recording_messages_are_schema_valid()
    {
        var validator = XsdValidator.TryLoad(XsdFactAttribute.XsdDir, XsdValidator.FeedSchema)!;
        foreach (var message in DemoMatch.Build())
        {
            Assert.Empty(validator.Validate(Encoding.UTF8.GetBytes(message.Body)));
        }
    }

    [XsdFact]
    public void System_messages_are_schema_valid()
    {
        var validator = XsdValidator.TryLoad(XsdFactAttribute.XsdDir, XsdValidator.FeedSchema)!;
        Assert.Empty(validator.Validate(FeedMessageBuilder.Alive(1, 1, true)));
        Assert.Empty(validator.Validate(FeedMessageBuilder.SnapshotComplete(1, 1, 5)));
        Assert.Empty(validator.Validate(FeedMessageBuilder.BetStop(1, "sr:match:1", 1, "all", MarketStatus.Suspended)));
    }

    [XsdFact]
    public void Static_description_files_are_schema_valid()
    {
        var validator = XsdValidator.TryLoad(XsdFactAttribute.XsdDir, XsdValidator.DescriptionsSchema)!;
        var files = Directory.GetFiles(DescriptionsDir, "*.xml");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var errors = validator.Validate(File.ReadAllBytes(file));
            Assert.True(errors.Count == 0, $"{Path.GetFileName(file)}: {string.Join("; ", errors)}");
        }
    }

    [XsdFact]
    public void Validator_rejects_invalid_and_unknown_documents()
    {
        var validator = XsdValidator.TryLoad(XsdFactAttribute.XsdDir, XsdValidator.FeedSchema)!;
        Assert.NotEmpty(validator.Validate(Encoding.UTF8.GetBytes("<alive product=\"1\" timestamp=\"1\"/>")));
        Assert.NotEmpty(validator.Validate(Encoding.UTF8.GetBytes("<not_a_feed_message/>")));
    }
}
