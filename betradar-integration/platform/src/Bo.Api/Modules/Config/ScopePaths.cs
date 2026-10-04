using Bo.Api.Infrastructure;
using Bo.Core.Config;
using Dapper;
using Npgsql;

namespace Bo.Api.Modules.Config;

/// <summary>Where a value is asked for; deeper ids fill in the parents from <c>sb</c> (market → event → tournament → category → sport).</summary>
public sealed record ScopeQuery(
    long? BrandId = null, long? SportId = null, long? CategoryId = null, long? TournamentId = null,
    long? EventId = null, long? MarketId = null, int? MarketTypeId = null)
{
    public static ScopeQuery ForScope(ScopeType scope, long? id, int? marketTypeId) => scope switch
    {
        ScopeType.Brand => new ScopeQuery(BrandId: id, MarketTypeId: marketTypeId),
        ScopeType.Sport => new ScopeQuery(SportId: id, MarketTypeId: marketTypeId),
        ScopeType.Category => new ScopeQuery(CategoryId: id, MarketTypeId: marketTypeId),
        ScopeType.Tournament => new ScopeQuery(TournamentId: id, MarketTypeId: marketTypeId),
        ScopeType.Event => new ScopeQuery(EventId: id, MarketTypeId: marketTypeId),
        ScopeType.Market => new ScopeQuery(MarketId: id, MarketTypeId: marketTypeId),
        _ => new ScopeQuery(MarketTypeId: marketTypeId),
    };
}

public static class ScopePaths
{
    public static async Task<ScopeContext> BuildAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long operatorId, ScopeQuery q)
    {
        if (q.BrandId is { } brandId && !await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM bo.brand WHERE id = @brandId)", new { brandId }, tx))
        {
            throw BoProblem.NotFound("Brand");
        }

        long? sport = q.SportId, category = q.CategoryId, tournament = q.TournamentId, evt = q.EventId, market = q.MarketId;
        int? marketType = q.MarketTypeId;

        if (market is { } m)
        {
            var row = await conn.QuerySingleOrDefaultAsync<(long EventId, int MarketTypeId)>(
                "SELECT event_id, market_description_id FROM sb.market WHERE id = @m", new { m }, tx);
            if (row == default)
            {
                throw BoProblem.NotFound("Market");
            }
            evt = row.EventId;
            marketType ??= row.MarketTypeId;
        }
        if (evt is { } e)
        {
            var row = await conn.QuerySingleOrDefaultAsync<(long SportId, long? TournamentId)>(
                "SELECT sport_id::bigint, tournament_id::bigint FROM sb.event WHERE id = @e", new { e }, tx);
            if (row == default)
            {
                throw BoProblem.NotFound("Event");
            }
            sport = row.SportId;
            tournament = row.TournamentId;
        }
        if (tournament is { } t)
        {
            var row = await conn.QuerySingleOrDefaultAsync<(long SportId, long CategoryId)>(
                "SELECT sport_id::bigint, category_id::bigint FROM sb.tournament WHERE id = @t", new { t }, tx);
            if (row == default)
            {
                throw BoProblem.NotFound("Tournament");
            }
            sport = row.SportId;
            category = row.CategoryId;
        }
        if (category is { } c && sport is null)
        {
            sport = await conn.ExecuteScalarAsync<long?>("SELECT sport_id::bigint FROM sb.category WHERE id = @c", new { c }, tx)
                    ?? throw BoProblem.NotFound("Category");
        }
        return new ScopeContext(operatorId, q.BrandId, sport, category, tournament, evt, market, marketType);
    }
}
