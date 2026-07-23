using TestAtlas.Cli;
using Xunit;

namespace TestAtlas.Tests;

/// <summary>Exercises the spec §7 exit-code contract through the real command entry points.</summary>
public sealed class CliTests : IClassFixture<IndexedFixtureSolution>
{
    private readonly IndexedFixtureSolution _fx;
    public CliTests(IndexedFixtureSolution fx) => _fx = fx;

    [Fact]
    public void Index_missing_input_is_bad_args()
    {
        var (code, _) = Capture(() => Commands.RunIndex(new[] { "/no/such/path.sln" }));
        Assert.Equal(ExitCode.BadArgs, code);
    }

    [Fact]
    public void Index_clean_solution_returns_success_and_writes_file()
    {
        // Index loads a solution → runs through the real CLI process (MSBuildLocator can't run in
        // the vstest host). ExitCode.Success == 0.
        using var temp = new TempDir();
        var db = temp.File("out.db");
        var (code, _, _) = CliRunner.Index(FixturePaths.FixtureSolution, db, "--quiet");
        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(db));
    }

    [Fact]
    public void Index_partial_load_returns_warnings()
    {
        using var temp = new TempDir();
        var db = temp.File("broken.db");
        var (code, _, _) = CliRunner.Index(FixturePaths.BrokenSolution, db, "--quiet");
        Assert.Equal(ExitCode.Warnings, code);
    }

    [Fact]
    public void Validate_accepts_a_real_map()
    {
        var (code, _) = Capture(() => Commands.RunValidate(new[] { _fx.DbPath }));
        Assert.Equal(ExitCode.Success, code);
    }

    [Fact]
    public void Validate_rejects_a_non_map_file()
    {
        using var temp = new TempDir();
        var junk = temp.File("junk.db");
        File.WriteAllText(junk, "definitely not sqlite");
        var (code, _) = Capture(() => Commands.RunValidate(new[] { junk }));
        Assert.Equal(ExitCode.Fatal, code);
    }

    [Fact]
    public void Stats_reports_the_totals_and_returns_success()
    {
        var (code, stdout) = Capture(() => Commands.RunStats(new[] { _fx.DbPath }));
        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("2 project(s), 15 class(es), 10 method(s)", stdout);
        Assert.Contains("Fixture.SpecFlow", stdout);
    }

    [Fact]
    public void Report_writes_a_self_contained_html_file_and_returns_success()
    {
        using var temp = new TempDir();
        var html = temp.File("report.html");
        var (code, stdout) = Capture(() => Commands.RunReport(new[] { _fx.DbPath, "--html", html }));

        Assert.Equal(ExitCode.Success, code);
        Assert.True(File.Exists(html));
        var body = File.ReadAllText(html);

        // Self-contained: no external stylesheet/script/font references.
        Assert.DoesNotContain("<link", body);
        Assert.DoesNotContain("src=\"http", body);
        Assert.DoesNotContain("href=\"http", body);

        // Vacuity: real map content is present, not just a shell.
        Assert.Contains("<!doctype html>", body);
        Assert.Contains("Login", body);
        Assert.Contains("pigs can fly", body);            // the unbound step
        Assert.Contains("no matching step definition", body);
        Assert.Contains("2 candidates", body);            // the ambiguous step
        Assert.Contains("report.html", stdout);
    }

    [Fact]
    public void Report_missing_map_is_bad_args()
    {
        var (code, _) = Capture(() => Commands.RunReport(new[] { "/no/such/map.db" }));
        Assert.Equal(ExitCode.BadArgs, code);
    }

    // ---- pure arg-parsing contract -----------------------------------------------------------
    [Fact]
    public void ArgParser_accepts_the_happy_path()
    {
        Assert.True(ParsedArgs.TryParse(
            new[] { "My.sln", "--output", "map.db", "--include", "A", "--include", "B", "--exclude", "C", "--verbose" },
            out var p, out _));
        Assert.Equal("My.sln", p.Positional);
        Assert.Equal("map.db", p.Output);
        Assert.Equal(new[] { "A", "B" }, p.Include);
        Assert.Equal(new[] { "C" }, p.Exclude);
        Assert.True(p.Verbose);
    }

    [Theory]
    [InlineData(new object[] { new[] { "--nope" } })]                 // unknown option
    [InlineData(new object[] { new[] { "My.sln", "--output" } })]     // missing value
    [InlineData(new object[] { new[] { "--verbose", "--quiet" } })]   // mutually exclusive
    [InlineData(new object[] { new[] { "a.sln", "b.sln" } })]         // two positionals
    public void ArgParser_rejects_bad_usage(string[] args)
        => Assert.False(ParsedArgs.TryParse(args, out _, out _));

    private static (int code, string stdout) Capture(Func<int> run)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetError(TextWriter.Null);
        try
        {
            var code = run();
            return (code, sw.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }
}
