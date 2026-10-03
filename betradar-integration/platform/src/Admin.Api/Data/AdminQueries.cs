using Dapper;
using Npgsql;
using Platform.Canonical.Feed;

namespace Admin.Api.Data;

/// <summary>Read-only queries over the canonical schema for the Feed Ops admin.</summary>
public sealed class AdminQueries(NpgsqlDataSource db)
{
    private const int MaxPageSize = 200;

    public async Task<OverviewDto> OverviewAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var counts = await conn.QuerySingleAsync<(long Live, long Upcoming, long Active, long Suspended, long Messages, long Failed, DateTime? Last)>("""
            SELECT
              (SELECT count(*) FROM sb.event WHERE status = 'live'),
              (SELECT count(*) FROM sb.event WHERE status = 'not_started' AND scheduled_at > now() - interval '3 hours'),
              (SELECT count(*) FROM sb.market WHERE status = 'active'),
              (SELECT count(*) FROM sb.market WHERE status = 'suspended'),
              (SELECT count(*) FROM sb.feed_message_log WHERE received_at > now() - interval '1 hour'),
              (SELECT count(*) FROM sb.feed_message_log WHERE received_at > now() - interval '1 hour' AND status = 'failed'),
              (SELECT max(received_at) FROM sb.feed_message_log)
            """);
        return new OverviewDto(await ProducersAsync(conn), counts.Live, counts.Upcoming, counts.Active, counts.Suspended,
            counts.Messages, counts.Failed, Utc(counts.Last));
    }

    public async Task<IReadOnlyList<ProducerDto>> ProducersAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await ProducersAsync(conn);
    }

    private static async Task<IReadOnlyList<ProducerDto>> ProducersAsync(NpgsqlConnection conn) =>
        (await conn.QueryAsync<ProducerDto>("""
            SELECT producer_id::int AS Id, name AS Name, state::text AS State, down_reason AS DownReason,
                   last_processed_feed_ts AS LastProcessedFeedTs, updated_at AS UpdatedAt
            FROM sb.producer_status ORDER BY producer_id
            """)).ToList();

    private const string EventSelect = """
        SELECT e.id AS Id, pm.provider_entity_id AS Urn, s.name_i18n->>'en' AS Sport, cat.name_i18n->>'en' AS Category,
               t.name_i18n->>'en' AS Tournament, h.name AS Home, a.name AS Away, e.scheduled_at AS ScheduledAt,
               e.status::text AS Status, e.match_status_code AS MatchStatusCode, e.home_score AS HomeScore,
               e.away_score AS AwayScore, e.clock->>'match_time' AS Clock, e.last_feed_ts AS LastFeedAt,
               (SELECT count(*) FROM sb.market m WHERE m.event_id = e.id) AS Markets,
               (SELECT count(*) FROM sb.market m WHERE m.event_id = e.id AND m.status = 'active') AS ActiveMarkets
        FROM sb.event e
        JOIN sb.sport s ON s.id = e.sport_id
        LEFT JOIN sb.tournament t ON t.id = e.tournament_id
        LEFT JOIN sb.category cat ON cat.id = t.category_id
        LEFT JOIN sb.provider_mapping pm ON pm.provider_id = 1 AND pm.entity_type = 'event' AND pm.internal_id = e.id
        LEFT JOIN LATERAL (SELECT c.name_i18n->>'en' AS name FROM sb.event_competitor ec JOIN sb.competitor c ON c.id = ec.competitor_id
                           WHERE ec.event_id = e.id AND ec.position = 1) h ON true
        LEFT JOIN LATERAL (SELECT c.name_i18n->>'en' AS name FROM sb.event_competitor ec JOIN sb.competitor c ON c.id = ec.competitor_id
                           WHERE ec.event_id = e.id AND ec.position = 2) a ON true
        """;

    public async Task<PagedResult<EventSummaryDto>> EventsAsync(
        string? status, string? q, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize) = Paging(page, pageSize);
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<EventSummaryDto>($"""
            {EventSelect}
            WHERE (@status::text IS NULL OR e.status::text = @status)
              AND (@q::text IS NULL OR h.name ILIKE @q OR a.name ILIKE @q OR pm.provider_entity_id ILIKE @q
                   OR t.name_i18n->>'en' ILIKE @q)
              AND (@from::timestamptz IS NULL OR e.scheduled_at >= @from)
              AND (@to::timestamptz IS NULL OR e.scheduled_at < @to)
            ORDER BY CASE e.status WHEN 'live' THEN 0 WHEN 'suspended' THEN 0 ELSE 1 END, e.scheduled_at DESC NULLS LAST, e.id DESC
            LIMIT @limit OFFSET @offset
            """, new
        {
            status = Blank(status),
            q = Blank(q) is { } text ? $"%{text}%" : null,
            from,
            to,
            limit = pageSize + 1,
            offset = (page - 1) * pageSize,
        })).ToList();
        return new PagedResult<EventSummaryDto>(rows.Take(pageSize).ToList(), page, pageSize, rows.Count > pageSize);
    }

    public async Task<EventDetailDto?> EventAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var ev = await conn.QuerySingleOrDefaultAsync<EventSummaryDto>($"{EventSelect} WHERE e.id = @id", new { id });
        if (ev is null)
        {
            return null;
        }
        string[] competitors = [ev.Home ?? "{$competitor1}", ev.Away ?? "{$competitor2}"];

        var markets = (await conn.QueryAsync<MarketRow>("""
            SELECT m.id AS Id, pm.provider_entity_id AS MarketTypeId, d.name_template_i18n->>'en' AS Template,
                   m.specifiers AS Specifiers, m.status::text AS Status, m.feed_status::text AS FeedStatus,
                   m.source_producer_id AS ProducerId, m.is_favourite AS Favourite, m.last_feed_ts AS LastFeedAt
            FROM sb.market m
            JOIN sb.market_description d ON d.id = m.market_description_id
            LEFT JOIN sb.provider_mapping pm ON pm.provider_id = 1 AND pm.entity_type = 'market_type' AND pm.internal_id = d.id
            WHERE m.event_id = @id
            ORDER BY NULLIF(regexp_replace(pm.provider_entity_id, '\D', '', 'g'), '')::int NULLS LAST, m.specifiers
            """, new { id })).ToList();

        var outcomes = (await conn.QueryAsync<OutcomeRow>("""
            SELECT o.market_id AS MarketId, o.code AS Code, mdo.name_template_i18n->>'en' AS Template, o.odds AS Odds,
                   o.probability AS Probability, o.is_active AS Active, o.result::text AS Result, o.void_factor AS VoidFactor,
                   o.dead_heat_factor AS DeadHeatFactor, o.settlement_certainty AS Certainty
            FROM sb.outcome o
            JOIN sb.market m ON m.id = o.market_id
            LEFT JOIN sb.market_description_outcome mdo
                   ON mdo.market_description_id = m.market_description_id AND mdo.variant = '' AND mdo.code = o.code
            WHERE m.event_id = @id
            ORDER BY o.market_id, mdo.ordinal NULLS LAST, o.code
            """, new { id })).ToLookup(o => o.MarketId);

        return new EventDetailDto(ev, markets.Select(m => new MarketDto(
            m.Id, m.MarketTypeId, NameRenderer.Render(m.Template, m.Specifiers, competitors), m.Template, m.Specifiers,
            m.Status, m.FeedStatus, m.ProducerId, m.Favourite, Utc(m.LastFeedAt),
            outcomes[m.Id].Select(o => new OutcomeDto(
                o.Code, NameRenderer.Render(o.Template ?? o.Code, m.Specifiers, competitors), o.Odds, o.Probability, o.Active,
                o.Result, o.VoidFactor, o.DeadHeatFactor, o.Certainty)).ToList())).ToList());
    }

    public async Task<IReadOnlyList<SettlementDto>> SettlementsAsync(long eventId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var competitors = (await conn.QueryAsync<string>("""
            SELECT c.name_i18n->>'en' FROM sb.event_competitor ec JOIN sb.competitor c ON c.id = ec.competitor_id
            WHERE ec.event_id = @eventId ORDER BY ec.position
            """, new { eventId })).ToList();
        var rows = await conn.QueryAsync<SettlementRow>("""
            SELECT s.id AS Id, s.market_id AS MarketId, d.name_template_i18n->>'en' AS MarketTemplate, m.specifiers AS Specifiers,
                   s.outcome_code AS OutcomeCode, mdo.name_template_i18n->>'en' AS OutcomeTemplate, s.result::text AS Result,
                   s.certainty AS Certainty, s.void_factor AS VoidFactor, s.dead_heat_factor AS DeadHeatFactor,
                   s.producer_id AS ProducerId, s.feed_ts AS FeedTs, s.rolled_back_at AS RolledBackAt,
                   s.superseded_by_id AS SupersededById
            FROM sb.settlement s
            JOIN sb.market m ON m.id = s.market_id
            JOIN sb.market_description d ON d.id = m.market_description_id
            LEFT JOIN sb.market_description_outcome mdo
                   ON mdo.market_description_id = m.market_description_id AND mdo.variant = '' AND mdo.code = s.outcome_code
            WHERE s.event_id = @eventId
            ORDER BY s.feed_ts, s.id
            """, new { eventId });
        return rows.Select(r => new SettlementDto(
            r.Id, r.MarketId, NameRenderer.Render(r.MarketTemplate, r.Specifiers, competitors), r.OutcomeCode,
            NameRenderer.Render(r.OutcomeTemplate ?? r.OutcomeCode, r.Specifiers, competitors), r.Result, r.Certainty,
            r.VoidFactor, r.DeadHeatFactor, r.ProducerId, Utc(r.FeedTs), Utc(r.RolledBackAt), r.SupersededById,
            r.RolledBackAt is not null ? "rolled_back" : r.SupersededById is not null ? "superseded" : "effective")).ToList();
    }

    public async Task<IReadOnlyList<BetStopDto>> BetStopsAsync(long eventId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<BetStopDto>("""
            SELECT id AS Id, source AS Source, array_to_string(groups, '|') AS Groups, target_status::text AS TargetStatus,
                   affected_markets AS AffectedMarkets, producer_id AS ProducerId, feed_ts AS FeedTs
            FROM sb.bet_stop_log WHERE event_id = @eventId ORDER BY feed_ts DESC, id DESC
            """, new { eventId })).ToList();
    }

    private const string MessageSelect = """
        SELECT f.id AS Id, f.received_at AS ReceivedAt, f.message_type AS Type, f.event_urn AS EventUrn,
               pm.internal_id AS EventId, f.producer_id AS ProducerId, f.request_id AS RequestId, f.feed_ts AS FeedTs,
               f.status::text AS Status, f.error AS Error,
               extract(epoch FROM (f.received_at - f.feed_ts)) * 1000 AS LagMs
        FROM sb.feed_message_log f
        LEFT JOIN sb.provider_mapping pm ON pm.provider_id = f.provider_id AND pm.entity_type = 'event' AND pm.provider_entity_id = f.event_urn
        """;

    /// <summary>Newest first; offset paging with a has-more flag (no count over the partitioned log).</summary>
    public async Task<PagedResult<FeedMessageDto>> MessagesAsync(
        string? type, string? status, string? eventUrn, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize,
        CancellationToken ct)
    {
        (page, pageSize) = Paging(page, pageSize);
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<FeedMessageRow>($"""
            {MessageSelect}
            WHERE (@type::text IS NULL OR f.message_type = @type)
              AND (@status::text IS NULL OR f.status::text = @status)
              AND (@eventUrn::text IS NULL OR f.event_urn = @eventUrn)
              AND (@from::timestamptz IS NULL OR f.received_at >= @from)
              AND (@to::timestamptz IS NULL OR f.received_at < @to)
            ORDER BY f.received_at DESC, f.id DESC
            LIMIT @limit OFFSET @offset
            """, new
        {
            type = Blank(type),
            status = Blank(status),
            eventUrn = Blank(eventUrn),
            from,
            to,
            limit = pageSize + 1,
            offset = (page - 1) * pageSize,
        })).ToList();
        return new PagedResult<FeedMessageDto>(rows.Take(pageSize).Select(r => r.ToDto()).ToList(), page, pageSize, rows.Count > pageSize);
    }

    public async Task<FeedMessageDetailDto?> MessageAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<FeedMessageRow>(
            MessageSelect.Replace("SELECT f.id AS Id,", "SELECT f.payload AS Payload, f.id AS Id,") + " WHERE f.id = @id", new { id });
        return row is null ? null : new FeedMessageDetailDto(row.ToDto(), row.Payload ?? "");
    }

    private static (int Page, int PageSize) Paging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, MaxPageSize));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? t) => t is { } v ? Utc(v) : null;

    private sealed record MarketRow(long Id, string? MarketTypeId, string Template, string Specifiers, string Status,
        string FeedStatus, short? ProducerId, bool Favourite, DateTime LastFeedAt);

    private sealed record OutcomeRow(long MarketId, string Code, string? Template, decimal? Odds, decimal? Probability,
        bool Active, string? Result, decimal? VoidFactor, decimal? DeadHeatFactor, short? Certainty);

    private sealed record SettlementRow(long Id, long MarketId, string MarketTemplate, string Specifiers, string OutcomeCode,
        string? OutcomeTemplate, string Result, short Certainty, decimal? VoidFactor, decimal? DeadHeatFactor,
        short ProducerId, DateTime FeedTs, DateTime? RolledBackAt, long? SupersededById);

    private sealed class FeedMessageRow
    {
        public string? Payload { get; init; }
        public long Id { get; init; }
        public DateTime ReceivedAt { get; init; }
        public string Type { get; init; } = "";
        public string? EventUrn { get; init; }
        public long? EventId { get; init; }
        public short? ProducerId { get; init; }
        public long? RequestId { get; init; }
        public DateTime? FeedTs { get; init; }
        public string Status { get; init; } = "";
        public string? Error { get; init; }
        public double? LagMs { get; init; }

        public FeedMessageDto ToDto() =>
            new(Id, Utc(ReceivedAt), Type, EventUrn, EventId, ProducerId, RequestId, Utc(FeedTs), Status, Error, LagMs);
    }
}
