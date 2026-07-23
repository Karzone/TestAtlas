using System.Diagnostics;
using TestAtlas.Cli;
using TestAtlas.Core.Indexing;
using TestAtlas.Core.Model;
using TestAtlas.Core.Reporting;
using TestAtlas.Core.Storage;

// TestAtlas CLI (working verb: `testatlas`). Slice 1: `index`, `stats`, `validate`.
// Exit codes follow the spec §7 contract (see ExitCode).

if (args.Length == 0)
{
    Commands.PrintUsage();
    return ExitCode.BadArgs;
}

var command = args[0];
var rest = args.Skip(1).ToArray();

return command switch
{
    "index" => Commands.RunIndex(rest),
    "stats" => Commands.RunStats(rest),
    "validate" => Commands.RunValidate(rest),
    "report" => Commands.RunReport(rest),
    "search" => Commands.RunSearch(rest),
    "-h" or "--help" or "help" => Ok(Commands.PrintUsage),
    _ => Fail($"Unknown command '{command}'.")
};

static int Ok(Action a) { a(); return ExitCode.Success; }
static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    Commands.PrintUsage();
    return ExitCode.BadArgs;
}

/// <summary>Command implementations, kept out of top-level statements for testability/clarity.</summary>
namespace TestAtlas.Cli
{
public static class Commands
{
    public static int RunIndex(string[] args)
    {
        if (!ParsedArgs.TryParse(args, out var parsed, out var error))
            return BadArgs(error!);

        var quiet = parsed.Quiet;
        var sw = Stopwatch.StartNew();

        // Resolve the input: explicit path, or discover a single .sln/.csproj in cwd.
        if (!TryResolveInput(parsed.Positional, out var inputPath, out var resolveError))
            return BadArgs(resolveError!);

        var output = parsed.Output ?? Path.Combine(Directory.GetCurrentDirectory(), "codemap.db");

        var options = new IndexOptions
        {
            SolutionPath = inputPath,
            Include = parsed.Include,
            Exclude = parsed.Exclude,
            VerboseLog = parsed.Verbose ? Console.Error.WriteLine : null,
        };

        IndexResult result;
        try
        {
            result = new SolutionIndexer().IndexAsync(options).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: indexing failed: {ex.Message}");
            return ExitCode.Fatal;
        }

        try
        {
            SqliteMapWriter.Write(result, output);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not write map to '{output}': {ex.Message}");
            return ExitCode.Fatal;
        }

        sw.Stop();

        if (!quiet)
        {
            var errors = result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnings = result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            var unbound = result.Edges.Count(e => e.EdgeKind == EdgeKinds.Unbound);
            var bound = result.Edges.Where(e => e.EdgeKind == EdgeKinds.BindsTo).Select(e => e.FromId).Distinct().Count();
            Console.WriteLine(
                $"Indexed {result.Projects.Count} project(s): " +
                $"{result.Classes.Count} class(es), {result.Methods.Count} method(s), " +
                $"{result.StepDefinitions.Count} step definition(s). " +
                $"gherkin: {result.Features.Count} feature(s), {result.Scenarios.Count} scenario(s), " +
                $"{result.ScenarioSteps.Count} step(s) ({bound} bound, {unbound} unbound). " +
                $"diagnostics: {result.Diagnostics.Count} ({errors} error(s), {warnings} warning(s)). " +
                $"-> {output} ({sw.ElapsedMilliseconds} ms)");
            if (errors > 0)
                Console.WriteLine("Completed with warnings — some projects failed to load (see `testatlas stats`).");
            else if (warnings > 0)
                Console.WriteLine($"All projects loaded; {warnings} non-fatal warning(s) recorded (see `testatlas stats`).");
        }

        return result.Outcome switch
        {
            IndexOutcome.Success => ExitCode.Success,
            IndexOutcome.CompletedWithWarnings => ExitCode.Warnings,
            _ => ExitCode.Fatal,
        };
    }

    public static int RunStats(string[] args)
    {
        var dbPath = args.FirstOrDefault(a => !a.StartsWith('-'))
                     ?? Path.Combine(Directory.GetCurrentDirectory(), "codemap.db");
        if (!File.Exists(dbPath))
            return BadArgs($"map file not found: {dbPath}");

        MapDocument doc;
        try
        {
            doc = MapReader.Read(dbPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not read map '{dbPath}': {ex.Message}");
            return ExitCode.Fatal;
        }

        Console.WriteLine($"TestAtlas map: {dbPath} (schema v{doc.UserVersion})");
        if (doc.UserVersion != MapSchema.Version)
            Console.WriteLine(
                $"note: this map was written by schema v{doc.UserVersion}; this tool is v{MapSchema.Version}. " +
                "Re-run `testatlas index` to get the latest fields (kinds, step definitions).");
        if (doc.Meta.TryGetValue(MapSchema.MetaGeneratedUtc, out var gen))
            Console.WriteLine($"generated: {gen}");
        Console.WriteLine();

        Console.WriteLine($"{"project",-32} {"classes",8} {"methods",8}");
        foreach (var p in doc.Projects)
        {
            var classes = doc.Classes.Count(c => c.ProjectId == p.Id);
            var methods = doc.Methods.Count(m => m.ProjectId == p.Id);
            Console.WriteLine($"{Trunc(p.Name, 32),-32} {classes,8} {methods,8}");
        }

        Console.WriteLine();
        Console.WriteLine($"totals: {doc.Projects.Count} project(s), {doc.Classes.Count} class(es), " +
            $"{doc.Methods.Count} method(s), {doc.StepDefinitions.Count} step definition(s)");

        var kinds = doc.Classes.GroupBy(c => c.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key);
        Console.WriteLine("class kinds:");
        foreach (var g in kinds)
            Console.WriteLine($"  {g.Key,-14} {g.Count()}");

        Console.WriteLine();
        Console.WriteLine($"gherkin: {doc.Features.Count} feature(s), {doc.Scenarios.Count} scenario(s), {doc.ScenarioSteps.Count} step(s)");

        var unbound = doc.Edges.Count(e => e.EdgeKind == EdgeKinds.Unbound);
        var boundSteps = doc.Edges.Where(e => e.EdgeKind == EdgeKinds.BindsTo).Select(e => e.FromId).Distinct().Count();
        var ambiguousSteps = doc.Edges.Where(e => e.EdgeKind == EdgeKinds.BindsTo && e.Confidence == BindConfidence.Ambiguous)
            .Select(e => e.FromId).Distinct().Count();
        Console.WriteLine($"bound steps: {boundSteps}");
        Console.WriteLine($"unbound steps: {unbound}");
        Console.WriteLine($"ambiguous bindings: {ambiguousSteps}");

        var errors = doc.Diagnostics.Count(d => d.Severity == "error");
        var warnings = doc.Diagnostics.Count(d => d.Severity == "warning");
        Console.WriteLine($"diagnostics: {doc.Diagnostics.Count} ({errors} error(s), {warnings} warning(s))");

        // Grouped summary by severity + code (errors first) — readable even at thousands of rows.
        var groups = doc.Diagnostics
            .GroupBy(d => (d.Severity, d.Code))
            .Select(g => (g.Key.Severity, g.Key.Code, Count: g.Count()))
            .OrderByDescending(g => g.Severity == "error")
            .ThenByDescending(g => g.Count);
        foreach (var g in groups)
            Console.WriteLine($"  {g.Severity,-7} {g.Code,-26} {g.Count}");

        // Then the actual error messages (capped), since those are the ones worth reading in full.
        var errorRows = doc.Diagnostics.Where(d => d.Severity == "error").ToList();
        const int cap = 20;
        foreach (var d in errorRows.Take(cap))
            Console.WriteLine($"    - {d.Message}");
        if (errorRows.Count > cap)
            Console.WriteLine($"    … and {errorRows.Count - cap} more error(s)");

        return ExitCode.Success;
    }

    public static int RunValidate(string[] args)
    {
        var dbPath = args.FirstOrDefault(a => !a.StartsWith('-'))
                     ?? Path.Combine(Directory.GetCurrentDirectory(), "codemap.db");
        if (!File.Exists(dbPath))
            return BadArgs($"map file not found: {dbPath}");

        if (!MapReader.TryValidate(dbPath, out var version, out var error))
        {
            Console.Error.WriteLine($"invalid: {error}");
            return ExitCode.Fatal;
        }

        if (version != MapSchema.Version)
        {
            Console.Error.WriteLine(
                $"unsupported schema version {version} (this tool supports v{MapSchema.Version}). Re-run `testatlas index`.");
            return ExitCode.Fatal;
        }

        Console.WriteLine($"valid TestAtlas map (schema v{version}): {dbPath}");
        return ExitCode.Success;
    }

    public static int RunReport(string[] args)
    {
        var dbPath = args.FirstOrDefault(a => !a.StartsWith('-'))
                     ?? Path.Combine(Directory.GetCurrentDirectory(), "codemap.db");
        if (!File.Exists(dbPath))
            return BadArgs($"map file not found: {dbPath}");

        var output = OptionValue(args, "--html")
                     ?? OptionValue(args, "--output")
                     ?? Path.ChangeExtension(dbPath, ".html");

        MapDocument doc;
        try
        {
            doc = MapReader.Read(dbPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not read map '{dbPath}': {ex.Message}");
            return ExitCode.Fatal;
        }

        string html;
        try
        {
            html = HtmlReportBuilder.Build(doc);
            File.WriteAllText(output, html);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not write report to '{output}': {ex.Message}");
            return ExitCode.Fatal;
        }

        var steps = doc.ScenarioSteps.Count;
        var resolved = doc.Edges.Where(e => e.EdgeKind == EdgeKinds.BindsTo).Select(e => e.FromId).Distinct().Count();
        var coverage = steps == 0 ? 0 : (int)Math.Round(100.0 * resolved / steps);
        Console.WriteLine(
            $"Wrote report -> {output} " +
            $"({doc.Features.Count} feature(s), {doc.Scenarios.Count} scenario(s), {steps} step(s), {coverage}% bound). " +
            "Open it in any browser.");
        WarnIfStaleSchema(doc.UserVersion);
        return ExitCode.Success;
    }

    /// <summary>Shared note when a map predates the current schema — the report/search sees empty facets.</summary>
    private static void WarnIfStaleSchema(int version)
    {
        if (version < MapSchema.Version)
            Console.WriteLine(
                $"note: this map is schema v{version}; this tool is v{MapSchema.Version}. It predates newer data " +
                "(Gherkin features, step-binding coverage, search) — re-run `testatlas index` to populate them.");
    }

    public static int RunSearch(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith('-')).ToArray();
        string dbPath, query;
        if (positional.Length >= 2) { dbPath = positional[0]; query = positional[1]; }
        else if (positional.Length == 1) { dbPath = Path.Combine(Directory.GetCurrentDirectory(), "codemap.db"); query = positional[0]; }
        else return BadArgs("usage: testatlas search [<db>] <query> [--steps] [--scenarios]");

        if (!File.Exists(dbPath))
            return BadArgs($"map file not found: {dbPath}");

        // Default: search both facets; --steps / --scenarios narrows to one.
        var wantSteps = args.Contains("--steps") || !args.Contains("--scenarios");
        var wantScenarios = args.Contains("--scenarios") || !args.Contains("--steps");

        MapDocument doc;
        try
        {
            doc = MapReader.Read(dbPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not read map '{dbPath}': {ex.Message}");
            return ExitCode.Fatal;
        }

        var total = 0;

        if (wantSteps)
        {
            List<long> hits;
            try { hits = MapReader.SearchSteps(dbPath, query).ToList(); }
            catch (Exception ex) { return BadArgs($"invalid search query: {ex.Message}"); }

            var byId = doc.StepDefinitions.ToDictionary(d => (long)d.Id);
            var classById = doc.Classes.ToDictionary(c => c.Id);
            Console.WriteLine($"step definitions matching '{query}': {hits.Count}");
            foreach (var id in hits)
            {
                if (!byId.TryGetValue(id, out var d)) continue;
                var cls = classById.TryGetValue(d.ClassId, out var c) ? c.Name : "?";
                Console.WriteLine($"  [{d.Keyword}] {d.Expression}  ({cls}, {Path.GetFileName(d.FilePath)}:{d.LineStart})");
            }
            total += hits.Count;
        }

        if (wantScenarios)
        {
            List<long> hits;
            try { hits = MapReader.SearchScenarios(dbPath, query).ToList(); }
            catch (Exception ex) { return BadArgs($"invalid search query: {ex.Message}"); }

            var byId = doc.Scenarios.ToDictionary(s => (long)s.Id);
            var featureById = doc.Features.ToDictionary(f => f.Id);
            if (wantSteps) Console.WriteLine();
            Console.WriteLine($"scenarios matching '{query}': {hits.Count}");
            foreach (var id in hits)
            {
                if (!byId.TryGetValue(id, out var s)) continue;
                var feature = featureById.TryGetValue(s.FeatureId, out var f) ? f.Name : "?";
                Console.WriteLine($"  {feature} › {s.Name}  ({Path.GetFileName(s.FilePath)}:{s.LineStart})");
            }
            total += hits.Count;
        }

        if (total == 0)
            Console.WriteLine($"no matches for '{query}'.");
        WarnIfStaleSchema(doc.UserVersion);
        return ExitCode.Success;
    }

    private static string? OptionValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool TryResolveInput(string? positional, out string inputPath, out string? error)
    {
        error = null;
        inputPath = string.Empty;

        if (!string.IsNullOrWhiteSpace(positional))
        {
            if (!File.Exists(positional)) { error = $"input not found: {positional}"; return false; }
            inputPath = positional;
            return true;
        }

        var cwd = Directory.GetCurrentDirectory();
        var slns = Directory.GetFiles(cwd, "*.sln");
        if (slns.Length == 1) { inputPath = slns[0]; return true; }
        if (slns.Length > 1)
        {
            error = "multiple .sln files found; specify one explicitly.";
            return false;
        }

        var projs = Directory.GetFiles(cwd, "*.csproj");
        if (projs.Length == 1) { inputPath = projs[0]; return true; }

        error = "no .sln (or single .csproj) found in the current directory; specify a path.";
        return false;
    }

    private static int BadArgs(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return ExitCode.BadArgs;
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static void PrintUsage()
    {
        Console.WriteLine("""
            testatlas — static semantic map for .NET test-automation solutions

            usage:
              testatlas index [<path-to-.sln|.csproj>] [options]
                  --output <file>     output path (default ./codemap.db, overwritten atomically)
                  --config <file>     config file (default ./codemap.json if present)
                  --include <glob>    project name/path glob to include (repeatable)
                  --exclude <glob>    project name/path glob to exclude (repeatable)
                  --verbose           per-project progress
                  --quiet             errors only
              testatlas stats [<db>]      entity counts per project, unbound/ambiguous, diagnostics
              testatlas report [<db>]     write a self-contained HTML drill-down of the map
                  --html <file>       output path (default <db>.html)
              testatlas search [<db>] <query>   FTS5 search over step definitions and scenarios
                  --steps             step definitions only
                  --scenarios         scenarios only
              testatlas validate [<db>]   check the file is a supported TestAtlas map

            exit codes: 0 ok · 1 completed with warnings · 2 fatal · 3 bad arguments
            """);
    }
}
}
