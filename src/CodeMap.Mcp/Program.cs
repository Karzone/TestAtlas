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
//
// With no usable map the server STILL STARTS (McpServer.WithoutMap): the handshake and tools/list succeed and
// every tools/call answers with an error that says how to supply one. Releases before 0.1.11 exited with
// code 2 here — which most hosts report only as "server failed", with the usage text in a log nobody opens.
// From 0.1.12 each tools/call looks for the map again and re-reads one that was rebuilt: a host starts the
// server with the session, usually before `testatlas index` has run, and nobody reconnects it.

var dbPath = args.FirstOrDefault(a => !a.StartsWith('-'))
             ?? Environment.GetEnvironmentVariable("TESTATLAS_DB");

if (string.IsNullOrWhiteSpace(dbPath))
    dbPath = DiscoverMapInWorkingDirectory();

McpServer server;
if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
{
    Console.Error.WriteLine("usage: testatlas-mcp [atlas.db]");
    Console.Error.WriteLine("  Provide a TestAtlas map produced by `testatlas index`, via (in order):");
    Console.Error.WriteLine("    - a path argument,");
    Console.Error.WriteLine("    - the TESTATLAS_DB environment variable, or");
    Console.Error.WriteLine("    - a codemap.db (or atlas.db) in the current working directory.");
    Console.Error.WriteLine(string.IsNullOrWhiteSpace(dbPath)
        ? "testatlas-mcp: no map found — starting without one; every tool call will say so until one is built."
        : $"testatlas-mcp: map '{dbPath}' does not exist — starting without one; every tool call will say so until one is built.");
    server = McpServer.WithoutMap(string.IsNullOrWhiteSpace(dbPath) ? null : dbPath);
}
else
{
    try
    {
        server = new McpServer(dbPath);
    }
    catch (Exception ex)
    {
        // A map that exists but cannot be opened is a real fault, not a missing configuration: fail loudly.
        Console.Error.WriteLine($"failed to open map '{dbPath}': {ex.Message}");
        return 1;
    }

    Console.Error.WriteLine($"testatlas-mcp: serving '{dbPath}' over stdio (JSON-RPC).");
}

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

// Look for a conventional map file in the current working directory (codemap.db, then atlas.db).
static string? DiscoverMapInWorkingDirectory() => McpServer.DiscoverMap(Directory.GetCurrentDirectory());
