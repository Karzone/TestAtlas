using TestAtlas.Mcp;

// TestAtlas MCP server (v2): exposes a TestAtlas map to an AI agent over the Model Context Protocol.
// Transport is stdio JSON-RPC 2.0 — newline-delimited messages on stdin/stdout; diagnostics on stderr
// (stdout carries ONLY protocol frames).
//
// The map path is resolved in order: the first non-flag argument, then $TESTATLAS_DB, then a
// conventional map in the working directory (codemap.db, then atlas.db). The last step lets an MCP
// client launch `testatlas-mcp` with NO arguments when the map sits in the project/solution root.
//
//   testatlas-mcp atlas.db
//   TESTATLAS_DB=atlas.db testatlas-mcp
//   testatlas-mcp                     # auto-discovers ./codemap.db (or ./atlas.db)

var dbPath = args.FirstOrDefault(a => !a.StartsWith('-'))
             ?? Environment.GetEnvironmentVariable("TESTATLAS_DB");

if (string.IsNullOrWhiteSpace(dbPath))
    dbPath = DiscoverMapInWorkingDirectory();

if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
{
    Console.Error.WriteLine("usage: testatlas-mcp [atlas.db]");
    Console.Error.WriteLine("  Provide a TestAtlas map produced by `testatlas index`, via (in order):");
    Console.Error.WriteLine("    - a path argument,");
    Console.Error.WriteLine("    - the TESTATLAS_DB environment variable, or");
    Console.Error.WriteLine("    - a codemap.db (or atlas.db) in the current working directory.");
    return 2;
}

McpServer server;
try
{
    server = new McpServer(dbPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"failed to open map '{dbPath}': {ex.Message}");
    return 1;
}

Console.Error.WriteLine($"testatlas-mcp: serving '{dbPath}' over stdio (JSON-RPC).");

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var response = server.HandleLine(line);
    if (response is null) continue; // a notification — no reply
    Console.Out.WriteLine(response);
    Console.Out.Flush();
}

return 0;

// Look for a conventional map file in the current working directory. Prefers `codemap.db` (the CLI's
// default output name) and falls back to `atlas.db`. Returns null when neither is present.
static string? DiscoverMapInWorkingDirectory()
{
    foreach (var name in new[] { "codemap.db", "atlas.db" })
    {
        var candidate = Path.Combine(Directory.GetCurrentDirectory(), name);
        if (File.Exists(candidate))
            return candidate;
    }

    return null;
}
