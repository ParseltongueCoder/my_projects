namespace UofSim.Core.Routing;

/// <summary>Message types published on the UOF <c>unifiedfeed</c> exchange.</summary>
public static class MessageTypes
{
    public const string OddsChange = "odds_change";
    public const string BetStop = "bet_stop";
    public const string BetSettlement = "bet_settlement";
    public const string RollbackBetSettlement = "rollback_bet_settlement";
    public const string BetCancel = "bet_cancel";
    public const string RollbackBetCancel = "rollback_bet_cancel";
    public const string FixtureChange = "fixture_change";
    public const string Alive = "alive";
    public const string SnapshotComplete = "snapshot_complete";
}

public enum Priority { None, Hi, Lo }

/// <summary>Which interest segment(s) a message carries: pre-match, virtual and/or live.</summary>
public readonly record struct Interest(bool Prematch = false, bool Virtual = false, bool Live = false)
{
    public static readonly Interest None = new();
    public static readonly Interest PrematchOnly = new(Prematch: true);
    public static readonly Interest LiveOnly = new(Live: true);
}

/// <summary>
/// Builds 8-segment UOF routing keys:
/// <c>{priority}.{pre|virt|-}.{live|-}.{message_type}.{sport_id}.{urn_type}.{event_id}.{node_id}</c>.
/// </summary>
public static class RoutingKey
{
    private const string Dash = "-";

    public static string Build(
        string messageType,
        Priority priority = Priority.None,
        Interest interest = default,
        int? sportId = null,
        string? eventUrn = null,
        int? nodeId = null)
    {
        var seg2 = interest.Virtual ? "virt" : interest.Prematch ? "pre" : Dash;
        var seg3 = interest.Live ? "live" : Dash;
        var (urnType, eventId) = eventUrn is null ? (Dash, Dash) : SplitUrn(eventUrn);

        return string.Join('.',
            priority switch { Priority.Hi => "hi", Priority.Lo => "lo", _ => Dash },
            seg2,
            seg3,
            messageType,
            sportId?.ToString() ?? Dash,
            urnType,
            eventId,
            nodeId?.ToString() ?? Dash);
    }

    /// <summary>System messages (alive, snapshot_complete) carry no event data.</summary>
    public static string System(string messageType, int? nodeId = null) =>
        Build(messageType, nodeId: nodeId);

    /// <summary><c>sr:match:123</c> → (<c>sr:match</c>, <c>123</c>).</summary>
    public static (string UrnType, string Id) SplitUrn(string urn)
    {
        var idx = urn.LastIndexOf(':');
        if (idx <= 0 || idx == urn.Length - 1)
        {
            throw new ArgumentException($"Invalid URN '{urn}'", nameof(urn));
        }
        return (urn[..idx], urn[(idx + 1)..]);
    }
}
