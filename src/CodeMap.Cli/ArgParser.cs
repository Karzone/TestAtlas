namespace TestAtlas.Cli;

/// <summary>
/// Tiny hand-rolled argument parser — a dependency-light stand-in that covers exactly the spec §7
/// surface (positional path + a handful of options, repeatable include/exclude). Returns false and
/// a message on a usage error so the caller can exit <see cref="ExitCode.BadArgs"/>.
/// </summary>
public sealed class ParsedArgs
{
    public string? Positional { get; private set; }
    public string? Output { get; private set; }
    public string? Config { get; private set; }
    public List<string> Include { get; } = new();
    public List<string> Exclude { get; } = new();
    public bool Verbose { get; private set; }
    public bool Quiet { get; private set; }

    public static bool TryParse(IReadOnlyList<string> args, out ParsedArgs parsed, out string? error)
    {
        parsed = new ParsedArgs();
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--output" or "-o":
                    if (!Next(args, ref i, out var o)) { error = $"{a} requires a value."; return false; }
                    parsed.Output = o;
                    break;
                case "--config":
                    if (!Next(args, ref i, out var c)) { error = $"{a} requires a value."; return false; }
                    parsed.Config = c;
                    break;
                case "--include":
                    if (!Next(args, ref i, out var inc)) { error = $"{a} requires a value."; return false; }
                    parsed.Include.Add(inc);
                    break;
                case "--exclude":
                    if (!Next(args, ref i, out var exc)) { error = $"{a} requires a value."; return false; }
                    parsed.Exclude.Add(exc);
                    break;
                case "--verbose":
                    parsed.Verbose = true;
                    break;
                case "--quiet":
                    parsed.Quiet = true;
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        error = $"Unknown option '{a}'.";
                        return false;
                    }
                    if (parsed.Positional is not null)
                    {
                        error = $"Unexpected extra argument '{a}'.";
                        return false;
                    }
                    parsed.Positional = a;
                    break;
            }
        }

        if (parsed is { Verbose: true, Quiet: true })
        {
            error = "--verbose and --quiet are mutually exclusive.";
            return false;
        }

        return true;
    }

    private static bool Next(IReadOnlyList<string> args, ref int i, out string value)
    {
        if (i + 1 >= args.Count) { value = string.Empty; return false; }
        value = args[++i];
        return true;
    }
}
