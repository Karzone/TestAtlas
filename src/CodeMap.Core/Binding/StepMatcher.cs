using System.Text;
using System.Text.RegularExpressions;

namespace TestAtlas.Core.Binding;

/// <summary>A <see cref="StepBinding"/> with its expression precompiled to an anchored regex.</summary>
public sealed record CompiledBinding(StepBinding Binding, Regex Pattern);

/// <summary>The keyword a scenario step is spoken with, after And/But have been resolved.</summary>
public enum StepKeyword
{
    Given,
    When,
    Then
}

/// <summary>
/// The keyword a binding attribute declares. <see cref="StepDefinition"/> (Reqnroll/SpecFlow
/// <c>[StepDefinition]</c>) is a wildcard — it binds a step of any keyword; the other three bind
/// only their own keyword.
/// </summary>
public enum BindingKeyword
{
    Given,
    When,
    Then,
    StepDefinition
}

/// <summary>How a binding's expression text should be interpreted.</summary>
public enum ExpressionKind
{
    Regex,
    CucumberExpression
}

/// <summary>The confidence of a step→binding resolution (spec §5.2, <c>binds_to</c>).</summary>
public enum MatchConfidence
{
    /// <summary>No binding matched. Recorded as an <c>unbound</c> diagnostic row.</summary>
    Unbound,

    /// <summary>Exactly one binding matched.</summary>
    Exact,

    /// <summary>More than one binding matched — all are recorded.</summary>
    Ambiguous
}

/// <summary>
/// One binding attribute on a step-definition method. A method decorated with both
/// <c>[Given]</c> and <c>[When]</c> yields two <see cref="StepBinding"/> values — the caller
/// expands per-attribute; the matcher treats each independently (spec §5.1).
/// </summary>
/// <param name="Keyword">The attribute keyword (Given/When/Then/StepDefinition).</param>
/// <param name="Expression">The raw expression text as written in source.</param>
/// <param name="Kind">Whether <paramref name="Expression"/> is a regex or a cucumber expression.</param>
/// <param name="Reference">
/// Opaque caller-supplied label (e.g. "Class.Method#0") echoed back on a match so the caller can
/// tie the result to a StepDefinition entity. The matcher never interprets it.
/// </param>
public sealed record StepBinding(
    BindingKeyword Keyword,
    string Expression,
    ExpressionKind Kind,
    string Reference = "");

/// <summary>The scenario step being resolved (spec §5.1, ScenarioStep).</summary>
/// <param name="Keyword">Effective keyword — And/But already resolved (see <see cref="StepKeywords.Resolve"/>).</param>
/// <param name="Text">The step text (without the leading keyword).</param>
public sealed record ScenarioStepInput(StepKeyword Keyword, string Text);

/// <summary>A single binding that matched a step, with the parameter values captured from the text.</summary>
public sealed record BindingMatch(StepBinding Binding, IReadOnlyList<string> Parameters);

/// <summary>The outcome of matching one step against a set of candidate bindings.</summary>
public sealed record MatchResult(MatchConfidence Confidence, IReadOnlyList<BindingMatch> Matches)
{
    public static readonly MatchResult Unbound =
        new(MatchConfidence.Unbound, Array.Empty<BindingMatch>());
}

/// <summary>Helpers for turning raw Gherkin keywords into an effective <see cref="StepKeyword"/>.</summary>
public static class StepKeywords
{
    /// <summary>
    /// Resolve a raw step keyword to its effective binding keyword. Given/When/Then map directly;
    /// And/But/* inherit the previous primary keyword (Gherkin semantics). A leading And/But with
    /// no previous primary defaults to Given.
    /// </summary>
    public static StepKeyword Resolve(string rawKeyword, StepKeyword? previousPrimary)
    {
        var k = (rawKeyword ?? string.Empty).Trim().TrimEnd(':').ToLowerInvariant();
        return k switch
        {
            "given" => StepKeyword.Given,
            "when" => StepKeyword.When,
            "then" => StepKeyword.Then,
            // And / But / * and anything unrecognised inherit the running primary keyword.
            _ => previousPrimary ?? StepKeyword.Given
        };
    }
}

/// <summary>
/// The pure step-binding matcher (spec §5.2 <c>binds_to</c>). No Roslyn, no SQLite, no IO — input
/// is a step plus candidate bindings; output is the matches with a confidence. This is the heart
/// of "does the mapping work", so it is deliberately dependency-free and exhaustively tested. It
/// is NOT wired into the indexing pipeline in slice 1 — slice 2 does that; here it only has to
/// exist and be correct.
/// </summary>
public static class StepMatcher
{
    /// <summary>
    /// Match one scenario step against a set of candidate bindings. Returns every binding whose
    /// keyword is compatible and whose expression matches the whole step text; the confidence is
    /// Unbound (0 matches), Exact (1) or Ambiguous (&gt;1).
    /// </summary>
    public static MatchResult Match(ScenarioStepInput step, IEnumerable<StepBinding> bindings)
    {
        if (bindings is null) throw new ArgumentNullException(nameof(bindings));
        // Compile on the fly (dropping ones that fail to compile), then match. For hot paths that
        // reuse the same binding set across many steps, precompile once with Compile and use the
        // compiled overload instead.
        var compiled = bindings.Select(Compile).Where(c => c is not null).Select(c => c!).ToList();
        return Match(step, compiled);
    }

    /// <summary>Precompile a binding to its anchored regex once, for reuse across many steps.</summary>
    public static CompiledBinding? Compile(StepBinding binding)
    {
        if (binding is null) return null;
        var regex = TryBuildRegex(binding);
        return regex is null ? null : new CompiledBinding(binding, regex);
    }

    /// <summary>Match a step against precompiled bindings (the scalable path).</summary>
    public static MatchResult Match(ScenarioStepInput step, IReadOnlyList<CompiledBinding> compiled)
    {
        if (step is null) throw new ArgumentNullException(nameof(step));
        if (compiled is null) throw new ArgumentNullException(nameof(compiled));

        var text = step.Text ?? string.Empty;
        var matches = new List<BindingMatch>();

        foreach (var cb in compiled)
        {
            if (!KeywordCompatible(cb.Binding.Keyword, step.Keyword))
                continue;

            var m = cb.Pattern.Match(text);
            if (!m.Success)
                continue;

            var parameters = new List<string>(Math.Max(0, m.Groups.Count - 1));
            for (var g = 1; g < m.Groups.Count; g++)
            {
                if (m.Groups[g].Success)
                    parameters.Add(m.Groups[g].Value);
            }

            matches.Add(new BindingMatch(cb.Binding, parameters));
        }

        var confidence = matches.Count switch
        {
            0 => MatchConfidence.Unbound,
            1 => MatchConfidence.Exact,
            _ => MatchConfidence.Ambiguous
        };

        return new MatchResult(confidence, matches);
    }

    /// <summary>A binding binds a step if it is a wildcard [StepDefinition] or shares its keyword.</summary>
    private static bool KeywordCompatible(BindingKeyword binding, StepKeyword step)
        => binding == BindingKeyword.StepDefinition
           || (binding == BindingKeyword.Given && step == StepKeyword.Given)
           || (binding == BindingKeyword.When && step == StepKeyword.When)
           || (binding == BindingKeyword.Then && step == StepKeyword.Then);

    private static Regex? TryBuildRegex(StepBinding binding)
    {
        try
        {
            var pattern = binding.Kind == ExpressionKind.CucumberExpression
                ? CucumberExpression.ToRegex(binding.Expression)
                : AnchorRegex(binding.Expression ?? string.Empty);

            // Culture-invariant so matching is deterministic across machines/locales.
            return new Regex(pattern, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // Invalid regex/expression → treat as non-matching, never throw (spec G2 spirit).
            return null;
        }
    }

    /// <summary>
    /// A step-definition regex must match the whole step text (Reqnroll/SpecFlow anchor implicitly).
    /// Add <c>^</c>/<c>$</c> only when the author has not already anchored, so existing anchors and
    /// alternation like <c>^a$|^b$</c> are preserved.
    /// </summary>
    internal static string AnchorRegex(string pattern)
    {
        var p = pattern;
        if (!p.StartsWith("^", StringComparison.Ordinal)) p = "^" + p;
        if (!p.EndsWith("$", StringComparison.Ordinal)) p += "$";
        return p;
    }
}

/// <summary>
/// Converts the supported subset of Cucumber Expressions to an anchored .NET regex. Supported:
/// typed parameters (<c>{int} {float} {word} {string} {}</c> and any <c>{custom}</c>), optional
/// text (<c>(s)</c> → optional), alternation on whitespace-delimited words (<c>a/b</c>), and
/// backslash escaping of <c>{ } ( ) / \</c>. Unsupported constructs degrade to literal text rather
/// than throwing.
/// </summary>
internal static class CucumberExpression
{
    public static string ToRegex(string expression)
    {
        var expr = expression ?? string.Empty;
        var sb = new StringBuilder();
        sb.Append('^');

        // Split into whitespace-delimited segments; a single space in the expression is a literal
        // space in the output. Alternation (a/b) binds within one segment (Cucumber semantics).
        var segments = expr.Split(' ');
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0) sb.Append(' '); // literal space between segments
            sb.Append(ConvertSegment(segments[i]));
        }

        sb.Append('$');
        return sb.ToString();
    }

    private static string ConvertSegment(string segment)
    {
        // Alternation splits a segment on unescaped '/'. One alternative → no wrapper.
        var alternatives = SplitUnescaped(segment, '/');
        if (alternatives.Count == 1)
            return ConvertAlternative(alternatives[0]);

        var parts = alternatives.Select(ConvertAlternative);
        return "(?:" + string.Join("|", parts) + ")";
    }

    private static string ConvertAlternative(string text)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '\\':
                    // Escaped literal: emit the next char escaped for regex.
                    if (i + 1 < text.Length)
                    {
                        sb.Append(Regex.Escape(text[i + 1].ToString()));
                        i += 2;
                    }
                    else
                    {
                        sb.Append("\\\\");
                        i++;
                    }
                    break;

                case '{':
                    {
                        var close = text.IndexOf('}', i + 1);
                        if (close < 0) { sb.Append("\\{"); i++; break; }
                        var name = text.Substring(i + 1, close - i - 1);
                        sb.Append(ParameterPattern(name));
                        i = close + 1;
                        break;
                    }

                case '(':
                    {
                        var close = text.IndexOf(')', i + 1);
                        if (close < 0) { sb.Append("\\("); i++; break; }
                        var inner = text.Substring(i + 1, close - i - 1);
                        // Optional text: convert inner literally, wrap as optional non-capturing group.
                        sb.Append("(?:").Append(ConvertAlternative(inner)).Append(")?");
                        i = close + 1;
                        break;
                    }

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        return sb.ToString();
    }

    private static string ParameterPattern(string name) => name switch
    {
        "int" => "(-?\\d+)",
        "float" or "double" or "bigdecimal" => "(-?\\d*\\.?\\d+)",
        "word" => "([^\\s]+)",
        "string" => "(\"[^\"]*\"|'[^']*')",
        // Anonymous {} and any custom/unknown type: capture a non-empty run (non-greedy so
        // trailing literals in the same expression still bind).
        _ => "(.+?)"
    };

    /// <summary>Split on an unescaped delimiter, keeping escapes intact within the parts.</summary>
    private static List<string> SplitUnescaped(string s, char delimiter)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                current.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }

            if (c == delimiter)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString());
        return parts;
    }
}
