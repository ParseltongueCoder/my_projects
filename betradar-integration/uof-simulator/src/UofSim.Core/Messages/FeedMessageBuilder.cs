using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UofSim.Core.Messages;

/// <summary>
/// Builds UOF AMQP message bodies. Element and attribute order follows the
/// UnifiedFeed.xsd sequences so the output validates against the official schema.
/// </summary>
public static class FeedMessageBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static byte[] Alive(int product, long timestampMs, bool subscribed) =>
        Serialize(new XElement("alive",
            new XAttribute("product", product),
            new XAttribute("timestamp", timestampMs),
            new XAttribute("subscribed", subscribed ? 1 : 0)));

    public static byte[] SnapshotComplete(int product, long timestampMs, long requestId) =>
        Serialize(new XElement("snapshot_complete",
            new XAttribute("request_id", requestId),
            new XAttribute("product", product),
            new XAttribute("timestamp", timestampMs)));

    public static byte[] OddsChange(
        int product,
        string eventUrn,
        long timestampMs,
        IEnumerable<MarketOdds> markets,
        SportEventStatusSnapshot? status = null,
        long? requestId = null,
        int? bettingStatus = null,
        int? betstopReason = null)
    {
        var root = MessageRoot("odds_change", product, eventUrn, timestampMs, requestId);
        if (status is not null)
        {
            root.Add(SportEventStatus(status));
        }

        var odds = new XElement("odds",
            Optional("betting_status", bettingStatus),
            Optional("betstop_reason", betstopReason));
        foreach (var m in markets)
        {
            var market = new XElement("market",
                new XAttribute("id", m.Id),
                Optional("specifiers", m.Specifiers),
                m.Favourite ? new XAttribute("favourite", 1) : null,
                new XAttribute("status", m.Status));

            // Deactivated, settled and cancelled markets are sent without outcomes.
            if (m.Status is MarketStatus.Active or MarketStatus.Suspended)
            {
                foreach (var o in m.Outcomes)
                {
                    market.Add(new XElement("outcome",
                        new XAttribute("id", o.Id),
                        o.Odds is { } price && o.Active ? new XAttribute("odds", Num(price)) : null,
                        o.Probability is { } p ? new XAttribute("probabilities", Num(p)) : null,
                        new XAttribute("active", o.Active ? 1 : 0)));
                }
            }
            odds.Add(market);
        }
        root.Add(odds);
        return Serialize(root);
    }

    public static byte[] BetStop(
        int product,
        string eventUrn,
        long timestampMs,
        string groups = "all",
        int? marketStatus = null,
        long? requestId = null)
    {
        var root = MessageRoot("bet_stop", product, eventUrn, timestampMs, requestId);
        root.Add(new XAttribute("groups", groups), Optional("market_status", marketStatus));
        return Serialize(root);
    }

    public static byte[] BetSettlement(
        int product,
        string eventUrn,
        long timestampMs,
        int certainty,
        IEnumerable<SettlementMarket> markets,
        long? requestId = null)
    {
        var root = MessageRoot("bet_settlement", product, eventUrn, timestampMs, requestId);
        root.Add(new XAttribute("certainty", certainty));
        var outcomes = new XElement("outcomes");
        foreach (var m in markets)
        {
            outcomes.Add(new XElement("market",
                new XAttribute("id", m.Id),
                Optional("specifiers", m.Specifiers),
                m.Outcomes.Select(o => new XElement("outcome",
                    new XAttribute("id", o.Id),
                    new XAttribute("result", o.Result),
                    o.VoidFactor is { } vf ? new XAttribute("void_factor", Num(vf)) : null,
                    o.DeadHeatFactor is { } dh ? new XAttribute("dead_heat_factor", Num(dh)) : null))));
        }
        root.Add(outcomes);
        return Serialize(root);
    }

    public static byte[] RollbackBetSettlement(
        int product, string eventUrn, long timestampMs, IEnumerable<(int Id, string? Specifiers)> markets) =>
        Serialize(MarketList("rollback_bet_settlement", product, eventUrn, timestampMs, markets));

    public static byte[] BetCancel(
        int product, string eventUrn, long timestampMs, IEnumerable<(int Id, string? Specifiers)> markets, int? voidReason = null)
    {
        var root = MarketList("bet_cancel", product, eventUrn, timestampMs, markets);
        if (voidReason is not null)
        {
            foreach (var market in root.Elements("market"))
            {
                market.Add(new XAttribute("void_reason", voidReason));
            }
        }
        return Serialize(root);
    }

    /// <summary>Replaces the root <c>timestamp</c> attribute (used by the replayer to make recordings "live").</summary>
    public static byte[] WithTimestamp(string xml, long timestampMs)
    {
        var doc = XDocument.Parse(xml);
        doc.Root!.SetAttributeValue("timestamp", timestampMs);
        return Serialize(doc.Root);
    }

    public static byte[] Serialize(XElement root)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
        };
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(writer);
        }
        return ms.ToArray();
    }

    private static XElement MessageRoot(string name, int product, string eventUrn, long timestampMs, long? requestId) =>
        new(name,
            new XAttribute("product", product),
            new XAttribute("event_id", eventUrn),
            new XAttribute("timestamp", timestampMs),
            Optional("request_id", requestId));

    private static XElement MarketList(
        string name, int product, string eventUrn, long timestampMs, IEnumerable<(int Id, string? Specifiers)> markets)
    {
        var root = MessageRoot(name, product, eventUrn, timestampMs, null);
        root.Add(markets.Select(m => new XElement("market",
            new XAttribute("id", m.Id),
            Optional("specifiers", m.Specifiers))));
        return root;
    }

    private static XElement SportEventStatus(SportEventStatusSnapshot s)
    {
        var el = new XElement("sport_event_status",
            new XAttribute("status", s.Status),
            new XAttribute("match_status", s.MatchStatus),
            new XAttribute("home_score", s.HomeScore),
            new XAttribute("away_score", s.AwayScore));
        if (s.MatchTime is not null)
        {
            el.Add(new XElement("clock", new XAttribute("match_time", s.MatchTime)));
        }
        return el;
    }

    private static XAttribute? Optional(string name, object? value) =>
        value is null ? null : new XAttribute(name, value);

    private static string Num(double value) => value.ToString("0.#######", Inv);
}
