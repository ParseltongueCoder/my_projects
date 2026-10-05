namespace Offer.Core;

/// <summary>
/// Overround removal and margin application (docs/06 §4.2). Inputs are implied probabilities <c>q = 1/odds</c>.
/// <list type="bullet">
/// <item><c>power</c> (default): remove with <c>p = q^k</c>, <c>Σp = 1</c>; apply with <c>q' = p^k'</c>, <c>Σq' = 1 + M</c>.
/// Puts more margin on long shots, like the market does.</item>
/// <item><c>proportional</c>: <c>p = q / Σq</c>; <c>q' = p · (1 + M)</c>.</item>
/// <item><c>shin</c>: Shin (1993) with insider share <c>z</c>: remove with
/// <c>p = (√(z² + 4(1−z)q²/Σq) − z) / (2(1−z))</c>; apply with <c>q' = √(z·p + (1−z)p²) · S</c>, <c>S = Σ√(z·p + (1−z)p²)</c>, <c>S² = 1 + M</c>.</item>
/// </list>
/// All solvers are bisections on a monotone sum (60 iterations, error far below the 0.001 odds precision).
/// </summary>
public static class Margin
{
    public static readonly IReadOnlyList<string> Methods = ["power", "proportional", "shin"];

    private const int Iterations = 60;

    /// <summary>Fair probabilities (Σ = 1) from implied probabilities whose sum is the booksum (≥ 1).</summary>
    public static double[] Remove(IReadOnlyList<double> q, string method)
    {
        var sum = q.Sum();
        if (sum <= 1)
        {
            return q.Select(x => x / sum).ToArray();
        }
        switch (method)
        {
            case "proportional":
                return q.Select(x => x / sum).ToArray();
            case "shin":
            {
                // Σp(z) falls from √Σq (z = 0) towards 1 as z grows.
                double P(double z, double x) => (Math.Sqrt(z * z + 4 * (1 - z) * x * x / sum) - z) / (2 * (1 - z));
                var z = Bisect(z => q.Sum(x => P(z, x)) - 1, 0, 0.999);
                var p = q.Select(x => P(z, x)).ToArray();
                var total = p.Sum();
                return p.Select(x => x / total).ToArray();
            }
            default:
            {
                // Σ q^k falls as k grows (every q < 1).
                var k = Bisect(k => q.Sum(x => Math.Pow(x, k)) - 1, 1e-6, 20);
                return q.Select(x => Math.Pow(x, k)).ToArray();
            }
        }
    }

    /// <summary>Implied probabilities with booksum <c>1 + margin</c> from fair probabilities (Σ = 1).</summary>
    public static double[] Apply(IReadOnlyList<double> p, double margin, string method)
    {
        if (margin <= 0)
        {
            return p.ToArray();
        }
        switch (method)
        {
            case "proportional":
                return p.Select(x => x * (1 + margin)).ToArray();
            case "shin":
            {
                double S(double z) => p.Sum(x => Math.Sqrt(z * x + (1 - z) * x * x));
                if (S(1) * S(1) >= 1 + margin)
                {
                    // S² rises from 1 (z = 0) with z.
                    var z = Bisect(z => 1 + margin - S(z) * S(z), 0, 1);
                    var s = S(z);
                    return p.Select(x => Math.Sqrt(z * x + (1 - z) * x * x) * s).ToArray();
                }
                // Margin beyond what Shin can express for these prices: power method instead.
                return Apply(p, margin, "power");
            }
            default:
            {
                // Σ p^k rises from 1 (k = 1) as k falls.
                var k = Bisect(k => p.Sum(x => Math.Pow(x, k)) - (1 + margin), 1e-6, 1);
                return p.Select(x => Math.Pow(x, k)).ToArray();
            }
        }
    }

    /// <summary>Delta mode for open / incomplete markets: <c>o' = 1 + (o − 1)(1 − d)</c>.</summary>
    public static double Delta(double odds, double delta) => 1 + (odds - 1) * (1 - delta);

    /// <summary>Root of a function that is positive below the root and negative above it.</summary>
    private static double Bisect(Func<double, double> f, double lo, double hi)
    {
        for (var i = 0; i < Iterations; i++)
        {
            var mid = (lo + hi) / 2;
            if (f(mid) > 0)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return (lo + hi) / 2;
    }
}
