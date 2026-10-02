using System.Globalization;
using Platform.Canonical.Feed;
using Sportradar.OddsFeed.SDK.Entities.Rest;

namespace Uof.Adapter;

/// <summary>Builds an <see cref="EventRef"/> from the SDK's sport event (which calls the Sports API on demand).</summary>
public static class EventResolver
{
    public static async Task<EventRef> ResolveAsync(ISportEvent ev, CultureInfo culture)
    {
        var sportId = await ev.GetSportIdAsync();
        ILongTermEvent? tournament = ev is IMatch match ? await match.GetTournamentAsync() : null;

        var sport = tournament is null ? null : await tournament.GetSportAsync();
        ICategorySummary? category = tournament switch
        {
            ITournament t => await t.GetCategoryAsync(),
            IBasicTournament b => await b.GetCategoryAsync(),
            _ => null,
        };

        var competitors = new List<CompetitorRef>();
        if (ev is IMatch m)
        {
            competitors.Add(Competitor(await m.GetHomeCompetitorAsync(), "home", culture));
            competitors.Add(Competitor(await m.GetAwayCompetitorAsync(), "away", culture));
        }
        else if (ev is ICompetition competition && await competition.GetCompetitorsAsync() is { } all)
        {
            competitors.AddRange(all.Select(c => Competitor(c, null, culture)));
        }

        return new EventRef(
            ev.Id.ToString(),
            sportId.ToString(),
            sport?.GetName(culture) ?? sportId.ToString(),
            category?.Id.ToString(),
            category?.GetName(culture),
            category?.CountryCode,
            tournament?.Id.ToString(),
            tournament is null ? null : await tournament.GetNameAsync(culture),
            await ev.GetScheduledTimeAsync() is { } scheduled ? ToUtc(scheduled) : null,
            competitors);
    }

    private static CompetitorRef Competitor(ICompetitor c, string? qualifier, CultureInfo culture) =>
        new(c.Id.ToString(), c.GetName(culture), c.GetAbbreviation(culture), c.CountryCode, qualifier);

    private static DateTimeOffset ToUtc(DateTime t) =>
        new(t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToUniversalTime() : t.ToUniversalTime());
}
