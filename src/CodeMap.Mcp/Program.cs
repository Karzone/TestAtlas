using TestAtlas.Mcp;

// TestAtlas MCP server (v2): exposes a TestAtlas map to an AI agent over the Model Context Protocol.
// Transport is stdio JSON-RPC 2.0 — newline-delimited messages on stdin/stdout; diagnostics on stderr
// (stdout carries ONLY protocol frames). The map path is the first non-flag argument, or $TESTATLAS_DB.
//
//   testatlas-mcp atlas.db
//   TESTATLAS_DB=atlas.db testatlas-mcp

var dbPath = args.FirstOrDefault(a => !a.StartsWith('-'))
             ?? Environment.GetEnvironmentVariable("TESTATLAS_DB");

if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
{
    Console.Error.WriteLine("usage: testatlas-mcp <atlas.db>   (or set TESTATLAS_DB)");
    Console.Error.WriteLine("  a TestAtlas map file produced by `testatlas index`.");
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
