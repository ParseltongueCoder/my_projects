using System.Globalization;
using System.Text.RegularExpressions;

namespace Bo.Core.Cms;

/// <summary>
/// The small ICU MessageFormat subset of player texts (docs/06 §6.2): <c>{name}</c> inserts the value as is,
/// <c>{name, number}</c> formats it with two decimals and a dot (<c>150.00</c>).
/// </summary>
public static partial class MessageFormat
{
    [GeneratedRegex(@"\{\s*([A-Za-z][A-Za-z0-9]*)\s*(?:,\s*([a-z]+)\s*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex AnyBraces();

    /// <summary>Words that reveal how risk is managed; a customer-visible text must not contain them (docs/06 §6.5).</summary>
    public static readonly IReadOnlyList<string> InternalTerms =
        ["liability", "risk group", "stake factor", "sharp", "arbitrage", "referral", "ლაიაბილითი", "რისკ-ჯგუფ", "რისკის ჯგუფ"];

    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m =>
        {
            if (!values.TryGetValue(m.Groups[1].Value, out var raw))
            {
                return m.Value;
            }
            return m.Groups[2].Value == "number" && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? n.ToString("0.00", CultureInfo.InvariantCulture)
                : raw;
        });

    /// <summary>Null when the text is fine; else why not. Parameters may be left out, unknown ones are errors.</summary>
    public static string? Lint(string text, IReadOnlyList<string> parameters, bool customerVisible)
    {
        if (text.Count(c => c == '{') != text.Count(c => c == '}'))
        {
            return "Unbalanced braces";
        }
        foreach (Match m in AnyBraces().Matches(text))
        {
            var p = Placeholder().Match(m.Value);
            if (!p.Success || p.Length != m.Length)
            {
                return $"{m.Value} is not a parameter (use {{name}} or {{name, number}})";
            }
            if (!parameters.Contains(p.Groups[1].Value))
            {
                return parameters.Count == 0
                    ? $"Unknown parameter {m.Value}: this message has no parameters"
                    : $"Unknown parameter {m.Value} (available: {string.Join(", ", parameters.Select(x => $"{{{x}}}"))})";
            }
            if (p.Groups[2].Success && p.Groups[2].Value != "number")
            {
                return $"Unknown format '{p.Groups[2].Value}' in {m.Value} (only 'number')";
            }
        }
        if (customerVisible && InternalTerms.FirstOrDefault(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)) is { } leak)
        {
            return $"Players must not see internal terms ('{leak}')";
        }
        return null;
    }
}
