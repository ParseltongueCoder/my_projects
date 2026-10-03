using System.Globalization;
using System.Text.RegularExpressions;

namespace Platform.Canonical.Feed;

/// <summary>
/// Renders UOF market/outcome name templates: <c>{$competitor1}</c>, <c>{$competitor2}</c>, <c>{$event}</c>,
/// <c>{total}</c> (specifier as is), <c>{+hcp}</c> / <c>{-hcp}</c> (signed, negated) and <c>{!periodnr}</c> (ordinal).
/// </summary>
public static partial class NameRenderer
{
    public static string Render(string template, string specifiers, IReadOnlyList<string> competitors)
    {
        var values = specifiers.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);

        return Placeholder().Replace(template, m =>
        {
            var token = m.Groups[1].Value;
            switch (token)
            {
                case "$competitor1": return competitors.Count > 0 ? competitors[0] : m.Value;
                case "$competitor2": return competitors.Count > 1 ? competitors[1] : m.Value;
                case "$event": return competitors.Count > 1 ? $"{competitors[0]} v {competitors[1]}" : m.Value;
            }

            var op = token[0] is '+' or '-' or '!' ? token[0] : '\0';
            var name = op == '\0' ? token : token[1..];
            if (!values.TryGetValue(name, out var raw))
            {
                return m.Value;
            }
            if (op == '\0' || !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                return raw;
            }
            return op switch
            {
                '+' => Signed(n),
                '-' => Signed(-n),
                '!' => Ordinal((int)n),
                _ => raw,
            };
        });
    }

    private static string Signed(decimal n) =>
        (n > 0 ? "+" : "") + n.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Ordinal(int n) =>
        n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Placeholder();
}
