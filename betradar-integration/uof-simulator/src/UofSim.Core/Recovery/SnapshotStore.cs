using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;

namespace UofSim.Core.Recovery;

/// <summary>The message a recovery re-sends for one event, with the routing key it was first published on.</summary>
public sealed record SnapshotMessage(string EventUrn, int ProducerId, string RoutingKey, string Body);

/// <summary>
/// Keeps, per producer and event, the odds as the feed last left them: the latest <c>odds_change</c>,
/// with markets turned to suspended when a later <c>bet_stop</c> arrived. A recovery re-sends these,
/// which is what lets a consumer rebuild its state after a producer outage.
/// </summary>
public sealed class SnapshotStore
{
    private readonly ConcurrentDictionary<(int Producer, string Event), SnapshotMessage> _latest = new();

    public void Observe(string routingKey, byte[] body)
    {
        XElement root;
        try
        {
            root = XDocument.Parse(Encoding.UTF8.GetString(body)).Root!;
        }
        catch (System.Xml.XmlException)
        {
            return;
        }
        if ((string?)root.Attribute("event_id") is not { } eventUrn || (int?)root.Attribute("product") is not { } producer)
        {
            return;
        }

        var key = (producer, eventUrn);
        switch (root.Name.LocalName)
        {
            case "odds_change":
                _latest[key] = new SnapshotMessage(eventUrn, producer, routingKey, root.ToString(SaveOptions.DisableFormatting));
                break;

            case "bet_stop" when _latest.TryGetValue(key, out var last):
                var odds = XElement.Parse(last.Body);
                foreach (var market in odds.Descendants("market").Where(m => (string?)m.Attribute("status") is null or "1"))
                {
                    market.SetAttributeValue("status", -1);
                }
                _latest[key] = last with { Body = odds.ToString(SaveOptions.DisableFormatting) };
                break;
        }
    }

    public IReadOnlyList<SnapshotMessage> ForProducer(int producerId, string? eventUrn = null) =>
        _latest.Values
            .Where(s => s.ProducerId == producerId && (eventUrn is null || s.EventUrn == eventUrn))
            .OrderBy(s => s.EventUrn, StringComparer.Ordinal)
            .ToList();
}
