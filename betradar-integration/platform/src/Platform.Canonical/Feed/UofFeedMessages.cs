using System.Globalization;
using System.Xml.Linq;

namespace Platform.Canonical.Feed;

/// <summary>A UOF event message parsed from its raw XML into provider-neutral terms.</summary>
public abstract record FeedCommand(string MessageType, string EventUrn, int ProducerId, DateTimeOffset FeedTs, long? RequestId);

public sealed record EventStatusUpdate(string Status, int MatchStatusCode, decimal? HomeScore, decimal? AwayScore, string? MatchTime);

public sealed record OutcomeUpdate(string Code, decimal? Odds, decimal? Probability, bool Active);

/// <param name="Status">Canonical status, or null for "handed over" (-2): ownership moves, the status does not.</param>
public sealed record MarketUpdate(int MarketTypeId, string Specifiers, string? Status, bool Favourite, IReadOnlyList<OutcomeUpdate> Outcomes);

public sealed record OddsChange(
    string EventUrn, int ProducerId, DateTimeOffset FeedTs, long? RequestId,
    EventStatusUpdate? EventStatus, IReadOnlyList<MarketUpdate> Markets, int? BettingStatus, int? BetstopReason)
    : FeedCommand("odds_change", EventUrn, ProducerId, FeedTs, RequestId);

public sealed record BetStop(
    string EventUrn, int ProducerId, DateTimeOffset FeedTs, long? RequestId, IReadOnlyList<string> Groups, string TargetStatus)
    : FeedCommand("bet_stop", EventUrn, ProducerId, FeedTs, RequestId);

public sealed record OutcomeSettlement(string Code, string Result, decimal? VoidFactor, decimal? DeadHeatFactor);

public sealed record MarketSettlement(int MarketTypeId, string Specifiers, int? VoidReason, IReadOnlyList<OutcomeSettlement> Outcomes);

public sealed record BetSettlement(
    string EventUrn, int ProducerId, DateTimeOffset FeedTs, long? RequestId, int Certainty, IReadOnlyList<MarketSettlement> Markets)
    : FeedCommand("bet_settlement", EventUrn, ProducerId, FeedTs, RequestId);

public sealed record MarketRef(int MarketTypeId, string Specifiers, int? VoidReason);

public sealed record RollbackBetSettlement(
    string EventUrn, int ProducerId, DateTimeOffset FeedTs, long? RequestId, IReadOnlyList<MarketRef> Markets)
    : FeedCommand("rollback_bet_settlement", EventUrn, ProducerId, FeedTs, RequestId);

public sealed record BetCancel(
    string EventUrn, int ProducerId, DateTimeOffset FeedTs, long? RequestId,
    DateTimeOffset? StartTime, DateTimeOffset? EndTime, string? SupersededBy, IReadOnlyList<MarketRef> Markets)
    : FeedCommand("bet_cancel", EventUrn, ProducerId, FeedTs, RequestId);

/// <summary>Messages we archive but do not apply yet (fixture_change, rollback_bet_cancel).</summary>
public sealed record UnhandledMessage(string Type, string EventUrn, int ProducerId, DateTimeOffset FeedTs, long? RequestId)
    : FeedCommand(Type, EventUrn, ProducerId, FeedTs, RequestId);

/// <summary>
/// Parses raw UOF XML (as delivered by the SDK in <c>RawMessage</c>) into <see cref="FeedCommand"/>s.
/// Working from the raw XML keeps normalisation independent of the SDK's object model.
/// </summary>
public static class UofFeedParser
{
    public static FeedCommand Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("Empty message");
        var type = root.Name.LocalName;
        var eventUrn = Req(root, "event_id");
        var producer = int.Parse(Req(root, "product"), CultureInfo.InvariantCulture);
        var ts = FromMs(long.Parse(Req(root, "timestamp"), CultureInfo.InvariantCulture));
        var requestId = Long(root, "request_id");

        return type switch
        {
            "odds_change" => new OddsChange(eventUrn, producer, ts, requestId,
                ParseStatus(root.Element("sport_event_status")),
                root.Element("odds")?.Elements("market").Select(ParseOddsMarket).ToList() ?? [],
                Int(root.Element("odds"), "betting_status"),
                Int(root.Element("odds"), "betstop_reason")),

            "bet_stop" => new BetStop(eventUrn, producer, ts, requestId,
                ((string?)root.Attribute("groups") ?? "all").Split('|', StringSplitOptions.RemoveEmptyEntries),
                // Without market_status a bet_stop suspends (docs/02 §2).
                MarketStatus(Int(root, "market_status") ?? -1) ?? "suspended"),

            "bet_settlement" => new BetSettlement(eventUrn, producer, ts, requestId,
                Int(root, "certainty") ?? 1,
                root.Element("outcomes")?.Elements("market").Select(m => new MarketSettlement(
                    Int(m, "id") ?? 0,
                    NormalizeSpecifiers((string?)m.Attribute("specifiers")),
                    Int(m, "void_reason"),
                    m.Elements("outcome").Select(o => new OutcomeSettlement(
                        Req(o, "id"),
                        Int(o, "result") == 1 ? "won" : "lost",
                        Dec(o, "void_factor"),
                        Dec(o, "dead_heat_factor"))).ToList())).ToList() ?? []),

            "rollback_bet_settlement" => new RollbackBetSettlement(eventUrn, producer, ts, requestId, MarketRefs(root)),

            "bet_cancel" => new BetCancel(eventUrn, producer, ts, requestId,
                Long(root, "start_time") is { } start ? FromMs(start) : null,
                Long(root, "end_time") is { } end ? FromMs(end) : null,
                (string?)root.Attribute("superceded_by"),
                MarketRefs(root)),

            _ => new UnhandledMessage(type, eventUrn, producer, ts, requestId),
        };
    }

    /// <summary>Specifier order on the feed is not guaranteed; the canonical key sorts by name: <c>hcp=-1|total=2.5</c>.</summary>
    public static string NormalizeSpecifiers(string? specifiers) =>
        string.IsNullOrWhiteSpace(specifiers)
            ? ""
            : string.Join('|', specifiers.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .OrderBy(s => s[..Math.Max(0, s.IndexOf('='))], StringComparer.Ordinal));

    /// <summary>UOF market status → canonical market_status; null for "handed over" (-2).</summary>
    public static string? MarketStatus(int status) => status switch
    {
        1 => "active",
        0 => "deactivated",
        -1 => "suspended",
        -2 => null,
        -3 => "settled",
        -4 => "cancelled",
        _ => throw new FormatException($"Unknown market status {status}"),
    };

    public static string EventStatus(int status) => status switch
    {
        0 => "not_started",
        1 => "live",
        2 => "suspended",
        3 => "ended",
        4 => "closed",
        5 => "cancelled",
        6 => "delayed",
        7 => "interrupted",
        8 => "postponed",
        9 => "abandoned",
        _ => throw new FormatException($"Unknown event status {status}"),
    };

    private static MarketUpdate ParseOddsMarket(XElement m)
    {
        // A market without status in odds_change is active (docs/02 §2).
        var status = MarketStatus(Int(m, "status") ?? 1);
        return new MarketUpdate(
            Int(m, "id") ?? throw new FormatException("market without id"),
            NormalizeSpecifiers((string?)m.Attribute("specifiers")),
            status,
            Int(m, "favourite") == 1,
            m.Elements("outcome").Select(o => new OutcomeUpdate(
                Req(o, "id"),
                Dec(o, "odds"),
                Dec(o, "probabilities"),
                Int(o, "active") != 0)).ToList());
    }

    private static EventStatusUpdate? ParseStatus(XElement? s) =>
        s is null
            ? null
            : new EventStatusUpdate(
                EventStatus(Int(s, "status") ?? 0),
                Int(s, "match_status") ?? 0,
                Dec(s, "home_score"),
                Dec(s, "away_score"),
                (string?)s.Element("clock")?.Attribute("match_time"));

    private static List<MarketRef> MarketRefs(XElement root) =>
        root.Elements("market").Select(m => new MarketRef(
            Int(m, "id") ?? 0,
            NormalizeSpecifiers((string?)m.Attribute("specifiers")),
            Int(m, "void_reason"))).ToList();

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static string Req(XElement e, string name) =>
        (string?)e.Attribute(name) ?? throw new FormatException($"<{e.Name.LocalName}> without '{name}'");

    private static int? Int(XElement? e, string name) =>
        e?.Attribute(name) is { } a ? int.Parse(a.Value, CultureInfo.InvariantCulture) : null;

    private static long? Long(XElement e, string name) =>
        e.Attribute(name) is { } a ? long.Parse(a.Value, CultureInfo.InvariantCulture) : null;

    private static decimal? Dec(XElement e, string name) =>
        e.Attribute(name) is { } a ? decimal.Parse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture) : null;
}
