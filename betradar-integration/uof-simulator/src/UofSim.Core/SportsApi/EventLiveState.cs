using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;

namespace UofSim.Core.SportsApi;

/// <summary>Latest known state of an event, as last published on the feed.</summary>
public sealed record EventLiveState(int Status, int MatchStatus, int HomeScore, int AwayScore, string? MatchTime)
{
    public static readonly EventLiveState NotStarted = new(0, 0, 0, 0, null);
}

/// <summary>
/// Tracks event state from outgoing feed messages so the REST summary always agrees with the feed,
/// whichever source (replay, scenario) produced the messages.
/// </summary>
public sealed class EventStateStore
{
    private readonly ConcurrentDictionary<string, EventLiveState> _states = new();

    public EventLiveState Get(string eventUrn) => _states.GetValueOrDefault(eventUrn, EventLiveState.NotStarted);

    public IEnumerable<string> LiveEvents => _states.Where(kv => kv.Value.Status is 1 or 2).Select(kv => kv.Key);

    public void Observe(byte[] body)
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

        if (root.Name.LocalName != "odds_change" || root.Element("sport_event_status") is not { } ses)
        {
            return;
        }
        var eventUrn = (string?)root.Attribute("event_id");
        if (eventUrn is null)
        {
            return;
        }

        _states[eventUrn] = new EventLiveState(
            (int?)ses.Attribute("status") ?? 0,
            (int?)ses.Attribute("match_status") ?? 0,
            (int?)ses.Attribute("home_score") ?? 0,
            (int?)ses.Attribute("away_score") ?? 0,
            (string?)ses.Element("clock")?.Attribute("match_time"));
    }

    public void Reset(string eventUrn) => _states.TryRemove(eventUrn, out _);
}
