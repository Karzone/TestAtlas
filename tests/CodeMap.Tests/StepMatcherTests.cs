using TestAtlas.Core.Binding;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>
/// The binding semantics of TestAtlas, as a table. Each row is one scenario step matched against a
/// set of candidate bindings, with the expected confidence, the expected set of matched binding
/// references, and (where relevant) the parameter values captured from the step text. Read this
/// table to understand exactly what <c>binds_to</c> means — it is the contract, not the code.
/// </summary>
public sealed class StepMatcherTests
{
    // ---- readable builders -------------------------------------------------------------------
    private static StepBinding Rx(BindingKeyword kw, string expr, string reference)
        => new(kw, expr, ExpressionKind.Regex, reference);

    private static StepBinding Cuke(BindingKeyword kw, string expr, string reference)
        => new(kw, expr, ExpressionKind.CucumberExpression, reference);

    public sealed record Case(
        string Name,
        StepKeyword Keyword,
        string Text,
        StepBinding[] Bindings,
        MatchConfidence Expected,
        string[] ExpectedRefs,
        string[]? Params = null);

    // ---- THE TABLE ---------------------------------------------------------------------------
    private static readonly Case[] Table =
    {
        // ── regex: keyword compatibility & whole-text anchoring ──────────────────────────────
        new("regex exact match, one captured param",
            StepKeyword.Given, "a user named Alice",
            new[] { Rx(BindingKeyword.Given, "a user named (.*)", "named") },
            MatchConfidence.Exact, new[] { "named" }, Params: new[] { "Alice" }),

        new("regex keyword mismatch does not bind",
            StepKeyword.When, "a user named Alice",
            new[] { Rx(BindingKeyword.Given, "a user named (.*)", "named") },
            MatchConfidence.Unbound, Array.Empty<string>()),

        new("[StepDefinition] is a wildcard — binds any keyword",
            StepKeyword.Then, "do the thing",
            new[] { Rx(BindingKeyword.StepDefinition, "do the thing", "step") },
            MatchConfidence.Exact, new[] { "step" }),

        new("regex is anchored to the whole step text",
            StepKeyword.Given, "log in now",
            new[] { Rx(BindingKeyword.Given, "log in", "li") },
            MatchConfidence.Unbound, Array.Empty<string>()),

        new("author-supplied ^…$ anchors are respected",
            StepKeyword.Given, "log in",
            new[] { Rx(BindingKeyword.Given, "^log in$", "li") },
            MatchConfidence.Exact, new[] { "li" }),

        new("regex captures multiple params in order",
            StepKeyword.Given, "user Bob aged 30",
            new[] { Rx(BindingKeyword.Given, "user (.*) aged (\\d+)", "ua") },
            MatchConfidence.Exact, new[] { "ua" }, Params: new[] { "Bob", "30" }),

        // ── cucumber expressions: parameter types, optionals, alternation ────────────────────
        new("cucumber {int} with optional plural — plural form",
            StepKeyword.Given, "a cart with 3 items",
            new[] { Cuke(BindingKeyword.Given, "a cart with {int} item(s)", "cart") },
            MatchConfidence.Exact, new[] { "cart" }, Params: new[] { "3" }),

        new("cucumber {int} with optional plural — singular form",
            StepKeyword.Given, "a cart with 1 item",
            new[] { Cuke(BindingKeyword.Given, "a cart with {int} item(s)", "cart") },
            MatchConfidence.Exact, new[] { "cart" }, Params: new[] { "1" }),

        new("cucumber {string} captures the quoted value",
            StepKeyword.Then, "the order \"ORD-1\" is placed",
            new[] { Cuke(BindingKeyword.Then, "the order {string} is placed", "ord") },
            MatchConfidence.Exact, new[] { "ord" }, Params: new[] { "\"ORD-1\"" }),

        new("cucumber {word} captures a single word",
            StepKeyword.Given, "color is red",
            new[] { Cuke(BindingKeyword.Given, "color is {word}", "word") },
            MatchConfidence.Exact, new[] { "word" }, Params: new[] { "red" }),

        new("cucumber {float} captures a decimal",
            StepKeyword.Given, "price is 3.50",
            new[] { Cuke(BindingKeyword.Given, "price is {float}", "flt") },
            MatchConfidence.Exact, new[] { "flt" }, Params: new[] { "3.50" }),

        new("cucumber anonymous {} captures the run",
            StepKeyword.Given, "say hello",
            new[] { Cuke(BindingKeyword.Given, "say {}", "anon") },
            MatchConfidence.Exact, new[] { "anon" }, Params: new[] { "hello" }),

        new("cucumber alternation — first alternative",
            StepKeyword.When, "I click ok",
            new[] { Cuke(BindingKeyword.When, "I click ok/cancel", "alt") },
            MatchConfidence.Exact, new[] { "alt" }),

        new("cucumber alternation — second alternative",
            StepKeyword.When, "I click cancel",
            new[] { Cuke(BindingKeyword.When, "I click ok/cancel", "alt") },
            MatchConfidence.Exact, new[] { "alt" }),

        new("cucumber alternation — no alternative matches",
            StepKeyword.When, "I click maybe",
            new[] { Cuke(BindingKeyword.When, "I click ok/cancel", "alt") },
            MatchConfidence.Unbound, Array.Empty<string>()),

        new("cucumber keyword mismatch does not bind",
            StepKeyword.When, "a cart with 3 items",
            new[] { Cuke(BindingKeyword.Given, "a cart with {int} item(s)", "cart") },
            MatchConfidence.Unbound, Array.Empty<string>()),

        new("cucumber unrelated text does not bind",
            StepKeyword.Given, "totally different",
            new[] { Cuke(BindingKeyword.Given, "a cart with {int} items", "cart") },
            MatchConfidence.Unbound, Array.Empty<string>()),

        // ── ambiguity: multiple candidates matching the same step ────────────────────────────
        new("ambiguous — literal and pattern both match (both recorded)",
            StepKeyword.Given, "the system is ready",
            new[]
            {
                Rx(BindingKeyword.Given, "the system is ready", "exact"),
                Rx(BindingKeyword.Given, "the system is (.*)", "pattern"),
            },
            MatchConfidence.Ambiguous, new[] { "exact", "pattern" }),

        new("ambiguity only counts keyword-compatible candidates",
            StepKeyword.Given, "the system is ready",
            new[]
            {
                Rx(BindingKeyword.Given, "the system is ready", "g"),
                Rx(BindingKeyword.When, "the system is (.*)", "w"),
            },
            MatchConfidence.Exact, new[] { "g" }),

        // ── multiple binding attributes on ONE method (caller expands to two bindings) ───────
        new("[Given]+[When] on one method — a Given step picks the Given attribute",
            StepKeyword.Given, "reset",
            new[]
            {
                Rx(BindingKeyword.Given, "reset", "M#given"),
                Rx(BindingKeyword.When, "reset", "M#when"),
            },
            MatchConfidence.Exact, new[] { "M#given" }),

        new("[Given]+[When] on one method — a When step picks the When attribute",
            StepKeyword.When, "reset",
            new[]
            {
                Rx(BindingKeyword.Given, "reset", "M#given"),
                Rx(BindingKeyword.When, "reset", "M#when"),
            },
            MatchConfidence.Exact, new[] { "M#when" }),

        new("[StepDefinition] and [Given] both match — ambiguous",
            StepKeyword.Given, "reset",
            new[]
            {
                Rx(BindingKeyword.StepDefinition, "reset", "sd"),
                Rx(BindingKeyword.Given, "reset", "g"),
            },
            MatchConfidence.Ambiguous, new[] { "sd", "g" }),

        // ── edge cases: empty candidate set, malformed expression never throws ───────────────
        new("no candidate bindings at all — unbound",
            StepKeyword.Given, "anything",
            Array.Empty<StepBinding>(),
            MatchConfidence.Unbound, Array.Empty<string>()),

        new("malformed regex degrades to non-match, never throws",
            StepKeyword.Given, "x",
            new[] { Rx(BindingKeyword.Given, "([unclosed", "bad") },
            MatchConfidence.Unbound, Array.Empty<string>()),
    };

    public static IEnumerable<object[]> Cases() => Table.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Cases), DisableDiscoveryEnumeration = true)]
    public void Matches_as_specified(Case c)
    {
        var result = StepMatcher.Match(new ScenarioStepInput(c.Keyword, c.Text), c.Bindings);

        Assert.True(c.Expected == result.Confidence,
            $"[{c.Name}] expected confidence {c.Expected} but got {result.Confidence}");

        var actualRefs = result.Matches.Select(m => m.Binding.Reference).OrderBy(r => r, StringComparer.Ordinal);
        var expectedRefs = c.ExpectedRefs.OrderBy(r => r, StringComparer.Ordinal);
        Assert.Equal(expectedRefs, actualRefs);

        if (c.Params is not null)
        {
            var single = Assert.Single(result.Matches);
            Assert.Equal(c.Params, single.Parameters.ToArray());
        }
    }

    [Theory]
    [InlineData("Given", null, StepKeyword.Given)]
    [InlineData("When", StepKeyword.Given, StepKeyword.When)]
    [InlineData("Then", StepKeyword.When, StepKeyword.Then)]
    [InlineData("And", StepKeyword.When, StepKeyword.When)]     // And inherits the running primary
    [InlineData("But", StepKeyword.Then, StepKeyword.Then)]     // But inherits too
    [InlineData("And", null, StepKeyword.Given)]                // leading And/But defaults to Given
    [InlineData("*", StepKeyword.Then, StepKeyword.Then)]       // bullet step inherits
    [InlineData("Then:", StepKeyword.When, StepKeyword.Then)]   // trailing punctuation tolerated
    public void Resolve_maps_keywords(string raw, StepKeyword? previous, StepKeyword expected)
        => Assert.Equal(expected, StepKeywords.Resolve(raw, previous));
}
