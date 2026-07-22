using System.Diagnostics;
using TestAtlas.Cli;
using TestAtlas.Core.Indexing;
using TestAtlas.Core.Model;
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
            Console.WriteLine(
                $"Indexed {result.Projects.Count} project(s): " +
                $"{result.Classes.Count} class(es), {result.Methods.Count} method(s). " +
                $"unbound steps: 0, ambiguous bindings: 0. " +
                $"diagnostics: {result.Diagnostics.Count} ({errors} error(s)). " +
                $"-> {output} ({sw.ElapsedMilliseconds} ms)");
            if (errors > 0)
                Console.WriteLine("Completed with warnings — see diagnostics (`testatlas stats`).");
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
        Console.WriteLine($"totals: {doc.Projects.Count} project(s), {doc.Classes.Count} class(es), {doc.Methods.Count} method(s)");
        // Slice 1 has no scenario steps / bindings yet, so these are structurally zero.
        Console.WriteLine("unbound steps: 0");
        Console.WriteLine("ambiguous bindings: 0");

        var errors = doc.Diagnostics.Count(d => d.Severity == "error");
        Console.WriteLine($"diagnostics: {doc.Diagnostics.Count} ({errors} error(s))");
        foreach (var d in doc.Diagnostics)
            Console.WriteLine($"  [{d.Severity}] {d.Code}: {d.Message}");

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
              testatlas validate [<db>]   check the file is a supported TestAtlas map

            exit codes: 0 ok · 1 completed with warnings · 2 fatal · 3 bad arguments
            """);
    }
}
}
