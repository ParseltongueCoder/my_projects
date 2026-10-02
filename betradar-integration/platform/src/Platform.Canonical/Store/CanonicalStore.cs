using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Platform.Canonical.Feed;

namespace Platform.Canonical.Store;

public enum ApplyOutcome { Processed, Duplicate, Failed }

/// <summary>
/// Writes provider data into the canonical model (schema <c>sb</c>, docs/02 §6). Every feed message is
/// archived raw in <c>feed_message_log</c> first, then applied in its own transaction.
/// </summary>
public sealed class CanonicalStore(NpgsqlDataSource db, short providerId = CanonicalStore.BetradarUof)
{
    public const short BetradarUof = 1;

    private static readonly TimeSpan DedupWindow = TimeSpan.FromHours(1);
    private static readonly HashSet<string> SpecifierTypes = ["integer", "decimal", "string", "variable_text", "competitor", "player"];

    // ------------------------------------------------------------------ reference data

    public async Task SeedMarketDescriptionsAsync(IEnumerable<MarketDescriptionRef> descriptions, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var d in descriptions)
        {
            var id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO sb.market_description (code, name_template_i18n, groups, is_variant)
                VALUES (@Code, @Name::jsonb, @Groups, @IsVariant)
                ON CONFLICT (code) DO UPDATE SET name_template_i18n = EXCLUDED.name_template_i18n,
                    groups = EXCLUDED.groups, is_variant = EXCLUDED.is_variant, updated_at = now()
                RETURNING id
                """,
                new
                {
                    Code = MarketCode(d.Id),
                    Name = I18n(d.NameTemplate),
                    Groups = d.Groups.ToArray(),
                    IsVariant = d.Specifiers.Any(s => s.Name == "variant"),
                }, tx);
            await MapAsync(conn, tx, "market_type", d.Id.ToString(), id);

            for (var i = 0; i < d.Specifiers.Count; i++)
            {
                var s = d.Specifiers[i];
                await conn.ExecuteAsync("""
                    INSERT INTO sb.market_specifier_def (market_description_id, name, type, ordinal)
                    VALUES (@id, @Name, @Type::sb.specifier_type, @i)
                    ON CONFLICT (market_description_id, name) DO UPDATE SET type = EXCLUDED.type, ordinal = EXCLUDED.ordinal
                    """, new { id, s.Name, Type = SpecifierTypes.Contains(s.Type) ? s.Type : "string", i }, tx);
            }
            for (var i = 0; i < d.Outcomes.Count; i++)
            {
                var o = d.Outcomes[i];
                await conn.ExecuteAsync("""
                    INSERT INTO sb.market_description_outcome (market_description_id, variant, code, name_template_i18n, ordinal)
                    VALUES (@id, '', @Code, @Name::jsonb, @i)
                    ON CONFLICT (market_description_id, variant, code) DO UPDATE
                        SET name_template_i18n = EXCLUDED.name_template_i18n, ordinal = EXCLUDED.ordinal
                    """, new { id, o.Code, Name = I18n(o.NameTemplate), i }, tx);
            }
        }
        await tx.CommitAsync(ct);
    }

    public async Task<long?> FindEventIdAsync(string eventUrn, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await FindMappedAsync(conn, null, "event", eventUrn);
    }

    /// <summary>Creates (or finds) the event with its sport, category, tournament and competitors.</summary>
    public async Task<long> EnsureEventAsync(EventRef e, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (await FindMappedAsync(conn, tx, "event", e.EventUrn) is { } existing)
        {
            return existing;
        }

        var sportId = await EnsureMappedAsync(conn, tx, "sport", e.SportUrn, () => conn.ExecuteScalarAsync<long>("""
            INSERT INTO sb.sport (code, name_i18n) VALUES (@Code, @Name::jsonb)
            ON CONFLICT (code) DO UPDATE SET name_i18n = EXCLUDED.name_i18n RETURNING id
            """, new { Code = Slug(e.SportName, e.SportUrn), Name = I18n(e.SportName) }, tx));

        var categoryId = await EnsureMappedAsync(conn, tx, "category", e.CategoryUrn ?? $"{e.SportUrn}:other", () =>
            conn.ExecuteScalarAsync<long>("""
                INSERT INTO sb.category (sport_id, name_i18n, country_code) VALUES (@sportId, @Name::jsonb, @Country) RETURNING id
                """, new { sportId, Name = I18n(e.CategoryName ?? "Other"), Country = e.CategoryCountryCode }, tx));

        long? tournamentId = e.TournamentUrn is null
            ? null
            : await EnsureMappedAsync(conn, tx, "tournament", e.TournamentUrn, () => conn.ExecuteScalarAsync<long>("""
                INSERT INTO sb.tournament (sport_id, category_id, name_i18n) VALUES (@sportId, @categoryId, @Name::jsonb) RETURNING id
                """, new { sportId, categoryId, Name = I18n(e.TournamentName ?? e.TournamentUrn) }, tx));

        var eventId = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO sb.event (event_type, sport_id, tournament_id, scheduled_at)
            VALUES (@Type::sb.event_type, @sportId, @tournamentId, @Scheduled) RETURNING id
            """, new
        {
            Type = e.EventUrn.Contains(":stage:", StringComparison.Ordinal) ? "stage" : "match",
            sportId,
            tournamentId = (int?)tournamentId,
            e.Scheduled,
        }, tx);
        await MapAsync(conn, tx, "event", e.EventUrn, eventId);

        for (var i = 0; i < e.Competitors.Count; i++)
        {
            var c = e.Competitors[i];
            var competitorId = await EnsureMappedAsync(conn, tx, "competitor", c.Urn, () => conn.ExecuteScalarAsync<long>("""
                INSERT INTO sb.competitor (sport_id, name_i18n, abbreviation, country_code)
                VALUES (@sportId, @Name::jsonb, @Abbreviation, @CountryCode) RETURNING id
                """, new { sportId, Name = I18n(c.Name), c.Abbreviation, c.CountryCode }, tx));
            await conn.ExecuteAsync("""
                INSERT INTO sb.event_competitor (event_id, position, competitor_id, qualifier)
                VALUES (@eventId, @Position, @competitorId, @Qualifier::sb.competitor_qualifier)
                """, new { eventId, Position = i + 1, competitorId, Qualifier = c.Qualifier is "home" or "away" ? c.Qualifier : null }, tx);
        }

        await tx.CommitAsync(ct);
        return eventId;
    }

    public async Task SetProducerStateAsync(int producerId, string name, string state, string? reason, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO sb.producer_status (provider_id, producer_id, name, state, down_reason)
            VALUES (@providerId, @producerId, @name, @state::sb.producer_state, @reason)
            ON CONFLICT (provider_id, producer_id) DO UPDATE
                SET state = EXCLUDED.state, down_reason = EXCLUDED.down_reason, name = EXCLUDED.name, updated_at = now()
            """, new { providerId, producerId = (short)producerId, name, state, reason });
    }

    // ------------------------------------------------------------------ feed messages

    /// <summary>Archives the raw message and applies it. Never throws for a bad message: it is logged as failed.</summary>
    public async Task<ApplyOutcome> ApplyAsync(
        FeedCommand command, long eventId, string rawXml, string? routingKey = null, DateTimeOffset? sentTs = null,
        CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var sha = SHA256.HashData(Encoding.UTF8.GetBytes(rawXml));

        var duplicate = await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM sb.feed_message_log
                           WHERE payload_sha256 = @sha AND received_at > now() - @window AND status = 'processed')
            """, new { sha, window = DedupWindow });

        var (logId, receivedAt) = await conn.QuerySingleAsync<(long, DateTime)>("""
            INSERT INTO sb.feed_message_log (provider_id, producer_id, message_type, routing_key, event_urn, request_id,
                                             feed_ts, sent_ts, payload, payload_sha256, status)
            VALUES (@providerId, @Producer, @Type, @routingKey, @Urn, @RequestId, @FeedTs, @sentTs, @rawXml, @sha,
                    @Status::sb.feed_msg_status)
            RETURNING id, received_at
            """, new
        {
            providerId,
            Producer = (short)command.ProducerId,
            Type = command.MessageType,
            routingKey,
            Urn = command.EventUrn,
            command.RequestId,
            command.FeedTs,
            sentTs,
            rawXml,
            sha,
            Status = duplicate ? "skipped_duplicate" : "received",
        });
        if (duplicate)
        {
            return ApplyOutcome.Duplicate;
        }

        try
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var ctx = new ApplyContext(conn, tx, eventId, logId, command);
            switch (command)
            {
                case OddsChange m: await ApplyOddsChangeAsync(ctx, m); break;
                case BetStop m: await ApplyBetStopAsync(ctx, m); break;
                case BetSettlement m: await ApplySettlementAsync(ctx, m); break;
                case RollbackBetSettlement m: await ApplyRollbackAsync(ctx, m); break;
                case BetCancel m: await ApplyCancelAsync(ctx, m); break;
            }
            await conn.ExecuteAsync("""
                UPDATE sb.producer_status SET last_processed_feed_ts = GREATEST(last_processed_feed_ts, @FeedTs), updated_at = now()
                WHERE provider_id = @providerId AND producer_id = @Producer
                """, new { command.FeedTs, providerId, Producer = (short)command.ProducerId }, tx);
            await SetLogStatusAsync(conn, tx, logId, receivedAt, "processed", null);
            await tx.CommitAsync(ct);
            return ApplyOutcome.Processed;
        }
        catch (Exception ex) when (ex is PostgresException or FormatException or InvalidOperationException)
        {
            await SetLogStatusAsync(conn, null, logId, receivedAt, "failed", ex.Message);
            return ApplyOutcome.Failed;
        }
    }

    private sealed record EffectiveSettlement(long Id, string Result, short Certainty, decimal? VoidFactor, decimal? DeadHeat);

    private sealed record ApplyContext(NpgsqlConnection Conn, NpgsqlTransaction Tx, long EventId, long LogId, FeedCommand Command);

    private async Task ApplyOddsChangeAsync(ApplyContext c, OddsChange m)
    {
        if (m.EventStatus is { } s)
        {
            await c.Conn.ExecuteAsync("""
                UPDATE sb.event SET status = @Status::sb.event_status, match_status_code = @MatchStatusCode,
                    home_score = @HomeScore, away_score = @AwayScore, clock = @Clock::jsonb,
                    last_feed_ts = @FeedTs, version = version + 1, updated_at = now()
                WHERE id = @EventId AND (last_feed_ts IS NULL OR last_feed_ts <= @FeedTs)
                """, new
            {
                s.Status,
                s.MatchStatusCode,
                s.HomeScore,
                s.AwayScore,
                Clock = s.MatchTime is null ? null : JsonSerializer.Serialize(new { match_time = s.MatchTime }),
                m.FeedTs,
                c.EventId,
            }, c.Tx);
        }

        foreach (var market in m.Markets)
        {
            var descriptionId = await EnsureMarketDescriptionAsync(c, market.MarketTypeId);
            var marketId = await c.Conn.ExecuteScalarAsync<long?>("""
                INSERT INTO sb.market AS mk (event_id, market_description_id, specifiers, specifiers_json, status,
                                             source_producer_id, is_favourite, last_feed_ts)
                VALUES (@EventId, @descriptionId, @Specifiers, @Json::jsonb, COALESCE(@Status, 'suspended')::sb.market_status,
                        @Producer, @Favourite, @FeedTs)
                ON CONFLICT ON CONSTRAINT market_nk DO UPDATE SET
                    -- odds_change never reopens a settled/cancelled market; only a rollback does.
                    status = CASE WHEN mk.status IN ('settled', 'cancelled') THEN mk.status
                                  ELSE COALESCE(@Status::sb.market_status, mk.status) END,
                    source_producer_id = EXCLUDED.source_producer_id,
                    is_favourite = EXCLUDED.is_favourite,
                    last_feed_ts = EXCLUDED.last_feed_ts,
                    version = mk.version + 1,
                    updated_at = now()
                WHERE mk.last_feed_ts <= EXCLUDED.last_feed_ts
                RETURNING id
                """, new
            {
                c.EventId,
                descriptionId,
                market.Specifiers,
                Json = SpecifiersJson(market.Specifiers),
                market.Status,
                Producer = (short)m.ProducerId,
                Favourite = market.Favourite,
                m.FeedTs,
            }, c.Tx);
            if (marketId is null)
            {
                continue; // older than what we already have (stale)
            }

            foreach (var o in market.Outcomes)
            {
                await c.Conn.ExecuteAsync("""
                    INSERT INTO sb.outcome AS oc (market_id, code, description_outcome_id, odds, probability, is_active, odds_updated_at)
                    VALUES (@marketId, @Code,
                            (SELECT id FROM sb.market_description_outcome
                             WHERE market_description_id = @descriptionId AND variant = '' AND code = @Code),
                            @Odds, @Probability, @Active, @FeedTs)
                    ON CONFLICT (market_id, code) DO UPDATE SET odds = EXCLUDED.odds, probability = EXCLUDED.probability,
                        is_active = EXCLUDED.is_active, odds_updated_at = EXCLUDED.odds_updated_at
                    WHERE oc.odds_updated_at IS NULL OR oc.odds_updated_at <= EXCLUDED.odds_updated_at
                    """, new { marketId, o.Code, descriptionId, o.Odds, o.Probability, o.Active, m.FeedTs }, c.Tx);
            }
        }
    }

    private async Task ApplyBetStopAsync(ApplyContext c, BetStop m)
    {
        var all = m.Groups.Contains("all");
        var affected = await c.Conn.ExecuteAsync("""
            UPDATE sb.market mk SET status = @TargetStatus::sb.market_status, version = mk.version + 1, updated_at = now()
            FROM sb.market_description d
            WHERE d.id = mk.market_description_id AND mk.event_id = @EventId AND mk.status = 'active'
              AND (@all OR d.groups && @Groups)
            """, new { m.TargetStatus, c.EventId, all, Groups = m.Groups.ToArray() }, c.Tx);

        await c.Conn.ExecuteAsync("""
            INSERT INTO sb.bet_stop_log (event_id, producer_id, source, groups, target_status, affected_markets, feed_ts, feed_message_id)
            VALUES (@EventId, @Producer, 'bet_stop', @Groups, @TargetStatus::sb.market_status, @affected, @FeedTs, @LogId)
            """, new { c.EventId, Producer = (short)m.ProducerId, Groups = m.Groups.ToArray(), m.TargetStatus, affected, m.FeedTs, c.LogId }, c.Tx);
    }

    private async Task ApplySettlementAsync(ApplyContext c, BetSettlement m)
    {
        foreach (var market in m.Markets)
        {
            var marketId = await EnsureMarketAsync(c, market.MarketTypeId, market.Specifiers);
            await c.Conn.ExecuteAsync("""
                UPDATE sb.market SET
                    status_before_close = CASE WHEN status IN ('settled', 'cancelled') THEN status_before_close ELSE status END,
                    status = 'settled', settled_at = @FeedTs, void_reason = @VoidReason, version = version + 1, updated_at = now()
                WHERE id = @marketId
                """, new { m.FeedTs, market.VoidReason, marketId }, c.Tx);

            foreach (var o in market.Outcomes)
            {
                await c.Conn.ExecuteAsync("""
                    INSERT INTO sb.outcome (market_id, code, is_active) VALUES (@marketId, @Code, false)
                    ON CONFLICT (market_id, code) DO NOTHING
                    """, new { marketId, o.Code }, c.Tx);

                var current = await c.Conn.QuerySingleOrDefaultAsync<EffectiveSettlement>("""
                    SELECT id AS Id, result::text AS Result, certainty AS Certainty, void_factor AS VoidFactor,
                           dead_heat_factor AS DeadHeat
                    FROM sb.settlement
                    WHERE market_id = @marketId AND outcome_code = @Code AND rolled_back_at IS NULL AND superseded_by_id IS NULL
                    """, new { marketId, o.Code }, c.Tx);
                if (current is { } cur && cur.Result == o.Result && cur.Certainty == m.Certainty
                    && cur.VoidFactor == o.VoidFactor && cur.DeadHeat == o.DeadHeatFactor)
                {
                    continue; // re-delivery of the same settlement
                }

                // Supersede the effective settlement (certainty upgrade / resettlement). The FK is deferred (V002),
                // so the old row can point at the new id before the new row is inserted.
                var newId = await c.Conn.ExecuteScalarAsync<long>(
                    "SELECT nextval(pg_get_serial_sequence('sb.settlement', 'id'))", transaction: c.Tx);
                if (current is { } previous)
                {
                    await c.Conn.ExecuteAsync("UPDATE sb.settlement SET superseded_by_id = @newId WHERE id = @Id",
                        new { newId, previous.Id }, c.Tx);
                }
                await c.Conn.ExecuteAsync("""
                    INSERT INTO sb.settlement (id, event_id, market_id, outcome_code, result, void_factor, dead_heat_factor,
                                               certainty, void_reason, producer_id, feed_ts, feed_message_id)
                    OVERRIDING SYSTEM VALUE
                    VALUES (@newId, @EventId, @marketId, @Code, @Result::sb.outcome_result, @VoidFactor, @DeadHeatFactor,
                            @Certainty, @VoidReason, @Producer, @FeedTs, @LogId)
                    """, new
                {
                    newId, c.EventId, marketId, o.Code, o.Result, o.VoidFactor, o.DeadHeatFactor,
                    Certainty = (short)m.Certainty, market.VoidReason, Producer = (short)m.ProducerId, m.FeedTs, c.LogId,
                }, c.Tx);
                await c.Conn.ExecuteAsync("""
                    UPDATE sb.outcome SET result = @Result::sb.outcome_result, void_factor = @VoidFactor,
                        dead_heat_factor = @DeadHeatFactor, settlement_certainty = @Certainty, settled_at = @FeedTs
                    WHERE market_id = @marketId AND code = @Code
                    """, new { o.Result, o.VoidFactor, o.DeadHeatFactor, Certainty = (short)m.Certainty, m.FeedTs, marketId, o.Code }, c.Tx);
            }
        }
    }

    private async Task ApplyRollbackAsync(ApplyContext c, RollbackBetSettlement m)
    {
        foreach (var market in m.Markets)
        {
            var marketId = await EnsureMarketAsync(c, market.MarketTypeId, market.Specifiers);
            var rollbackId = await c.Conn.ExecuteScalarAsync<long>("""
                INSERT INTO sb.rollback (kind, event_id, market_id, producer_id, feed_ts, feed_message_id)
                VALUES ('settlement', @EventId, @marketId, @Producer, @FeedTs, @LogId) RETURNING id
                """, new { c.EventId, marketId, Producer = (short)m.ProducerId, m.FeedTs, c.LogId }, c.Tx);
            await c.Conn.ExecuteAsync("""
                UPDATE sb.settlement SET rolled_back_at = @FeedTs, rollback_id = @rollbackId
                WHERE market_id = @marketId AND rolled_back_at IS NULL AND superseded_by_id IS NULL;
                UPDATE sb.outcome SET result = NULL, void_factor = NULL, dead_heat_factor = NULL,
                    settlement_certainty = NULL, settled_at = NULL
                WHERE market_id = @marketId;
                UPDATE sb.market SET status = COALESCE(status_before_close, 'deactivated'), status_before_close = NULL,
                    settled_at = NULL, version = version + 1, updated_at = now()
                WHERE id = @marketId AND status = 'settled';
                """, new { m.FeedTs, rollbackId, marketId }, c.Tx);
        }
    }

    private async Task ApplyCancelAsync(ApplyContext c, BetCancel m)
    {
        foreach (var market in m.Markets)
        {
            var marketId = await EnsureMarketAsync(c, market.MarketTypeId, market.Specifiers);
            await c.Conn.ExecuteAsync("""
                INSERT INTO sb.market_cancellation (event_id, market_id, void_reason, start_time, end_time, superceded_by_urn,
                                                    producer_id, feed_ts, feed_message_id)
                VALUES (@EventId, @marketId, @VoidReason, @StartTime, @EndTime, @SupersededBy, @Producer, @FeedTs, @LogId);
                UPDATE sb.market SET
                    status_before_close = CASE WHEN status IN ('settled', 'cancelled') THEN status_before_close ELSE status END,
                    status = 'cancelled', cancelled_at = @FeedTs, void_reason = @VoidReason, version = version + 1, updated_at = now()
                WHERE id = @marketId;
                """, new
            {
                c.EventId, marketId, market.VoidReason, m.StartTime, m.EndTime, m.SupersededBy,
                Producer = (short)m.ProducerId, m.FeedTs, c.LogId,
            }, c.Tx);
        }
    }

    /// <summary>Finds a market or creates it (deactivated) when a settlement/cancel arrives for a market never priced.</summary>
    private async Task<long> EnsureMarketAsync(ApplyContext c, int marketTypeId, string specifiers)
    {
        var descriptionId = await EnsureMarketDescriptionAsync(c, marketTypeId);
        return await c.Conn.ExecuteScalarAsync<long>("""
            INSERT INTO sb.market (event_id, market_description_id, specifiers, specifiers_json, status, last_feed_ts)
            VALUES (@EventId, @descriptionId, @specifiers, @Json::jsonb, 'deactivated', @FeedTs)
            ON CONFLICT ON CONSTRAINT market_nk DO UPDATE SET updated_at = now()
            RETURNING id
            """, new { c.EventId, descriptionId, specifiers, Json = SpecifiersJson(specifiers), c.Command.FeedTs }, c.Tx);
    }

    /// <summary>Market types missing from the seeded descriptions get a placeholder until descriptions are refreshed.</summary>
    private async Task<long> EnsureMarketDescriptionAsync(ApplyContext c, int marketTypeId) =>
        await EnsureMappedAsync(c.Conn, c.Tx, "market_type", marketTypeId.ToString(), () => c.Conn.ExecuteScalarAsync<long>("""
            INSERT INTO sb.market_description (code, name_template_i18n) VALUES (@Code, @Name::jsonb)
            ON CONFLICT (code) DO UPDATE SET updated_at = now() RETURNING id
            """, new { Code = MarketCode(marketTypeId), Name = I18n($"Market {marketTypeId}") }, c.Tx));

    // ------------------------------------------------------------------ helpers

    private async Task<long?> FindMappedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string type, string externalId) =>
        await conn.ExecuteScalarAsync<long?>("""
            SELECT internal_id FROM sb.provider_mapping
            WHERE provider_id = @providerId AND entity_type = @type AND provider_entity_id = @externalId
            """, new { providerId, type, externalId }, tx);

    private async Task<long> EnsureMappedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string type, string externalId, Func<Task<long>> create)
    {
        if (await FindMappedAsync(conn, tx, type, externalId) is { } id)
        {
            return id;
        }
        var newId = await create();
        await MapAsync(conn, tx, type, externalId, newId);
        return newId;
    }

    private Task MapAsync(NpgsqlConnection conn, IDbTransaction tx, string type, string externalId, long internalId) =>
        conn.ExecuteAsync("""
            INSERT INTO sb.provider_mapping (provider_id, entity_type, provider_entity_id, internal_id)
            VALUES (@providerId, @type, @externalId, @internalId) ON CONFLICT DO NOTHING
            """, new { providerId, type, externalId, internalId }, tx);

    private static Task SetLogStatusAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long id, DateTime receivedAt, string status, string? error) =>
        conn.ExecuteAsync("""
            UPDATE sb.feed_message_log SET status = @status::sb.feed_msg_status, error = @error, processed_at = now()
            WHERE id = @id AND received_at = @receivedAt
            """, new { status, error, id, receivedAt }, tx);

    private static string MarketCode(int marketTypeId) => $"uof_{marketTypeId}";

    private static string I18n(string en) => JsonSerializer.Serialize(new Dictionary<string, string> { ["en"] = en });

    private static string SpecifiersJson(string specifiers) =>
        JsonSerializer.Serialize(specifiers.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : ""));

    private static string Slug(string name, string fallback)
    {
        var slug = new string(name.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        return slug.Length > 0 ? slug : fallback;
    }
}
