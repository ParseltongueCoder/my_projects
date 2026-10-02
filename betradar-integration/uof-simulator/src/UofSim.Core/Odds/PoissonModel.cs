namespace UofSim.Core.Odds;

/// <summary>Probabilities of the final result, given the current score and the goals still expected.</summary>
public sealed record FinalScoreProbabilities(double Home, double Draw, double Away, double Over25, double BothTeamsScore);

/// <summary>
/// Minimal football model for the simulator: independent Poisson goals for the remaining time.
/// Good enough to make odds move believably; the full L3 model (Dixon-Coles, more markets) is S4.
/// </summary>
public static class PoissonModel
{
    private const int MaxGoals = 10;
    private const int FullTime = 90;

    /// <param name="homeXg">Expected home goals over a full match.</param>
    /// <param name="awayXg">Expected away goals over a full match.</param>
    public static FinalScoreProbabilities Evaluate(double homeXg, double awayXg, int minute, int homeScore, int awayScore)
    {
        var remaining = Math.Clamp((FullTime - minute) / (double)FullTime, 0, 1);
        var home = Distribution(homeXg * remaining);
        var away = Distribution(awayXg * remaining);

        double pHome = 0, pDraw = 0, pAway = 0, pOver = 0, pBtts = 0;
        for (var h = 0; h <= MaxGoals; h++)
        {
            for (var a = 0; a <= MaxGoals; a++)
            {
                var p = home[h] * away[a];
                var finalHome = homeScore + h;
                var finalAway = awayScore + a;
                if (finalHome > finalAway) pHome += p;
                else if (finalHome == finalAway) pDraw += p;
                else pAway += p;
                if (finalHome + finalAway > 2.5) pOver += p;
                if (finalHome > 0 && finalAway > 0) pBtts += p;
            }
        }
        // The grid is truncated at MaxGoals; normalise so each market sums to one.
        var total = pHome + pDraw + pAway;
        return new FinalScoreProbabilities(pHome / total, pDraw / total, pAway / total, pOver / total, pBtts / total);
    }

    /// <summary>Decimal odds with a proportional bookmaker margin, rounded to two decimals.</summary>
    public static double ToOdds(double probability, double margin = 0.05) =>
        Math.Max(1.01, Math.Round(1 / (probability * (1 + margin)), 2));

    private static double[] Distribution(double lambda)
    {
        var p = new double[MaxGoals + 1];
        p[0] = Math.Exp(-lambda);
        for (var k = 1; k <= MaxGoals; k++)
        {
            p[k] = p[k - 1] * lambda / k;
        }
        return p;
    }
}
