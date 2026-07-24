# TestAtlas MCP server (v2)

Exposes a TestAtlas map (`atlas.db`) to an AI agent over the **Model Context Protocol**, so a coding
agent can ask the questions the CLI/report already answer — *"what breaks if I change this class?"*,
*"which scenarios hit this endpoint?"*, *"find the step that does X"* — without a human running a query.

Read-only. It never re-indexes or writes; it reads the same map the `report`/`map`/`impact` commands do,
via `MapReader` + `ImpactAnalyzer`. Ship identity: a dotnet tool `testatlas-mcp` (project `CodeMap.Mcp`,
namespace `TestAtlas.Mcp`).

## Transport

Hand-rolled **JSON-RPC 2.0 over stdio** — no external MCP SDK, keeping the tool dependency-free and
offline (the same ethos as the syntax-only indexer). Messages are newline-delimited on stdin/stdout;
**stdout carries only protocol frames**, diagnostics go to stderr. `McpServer.HandleLine` is pure — one
request line in, one response line out (or `null` for a notification) — so the entire tool surface is
unit-testable without touching stdio.

Methods: `initialize` (advertises `protocolVersion` + `serverInfo` + a `tools` capability),
`notifications/initialized` (no reply), `tools/list`, `tools/call`, `ping`. Unknown methods → JSON-RPC
`-32601`; malformed input → `-32700`; any tool fault → `-32603` (the loop never dies). A tool result is a
single text content block whose text is the JSON payload the agent parses.

## Running

```
testatlas-mcp atlas.db            # map path as the first argument
TESTATLAS_DB=atlas.db testatlas-mcp
```

Wire it into an agent's MCP config as a stdio server whose command is `testatlas-mcp` with the map path
as an argument (or `TESTATLAS_DB` in its environment).

## Tools (slice 1)

| Tool | Arguments | Returns |
|---|---|---|
| `stats` | — | Project/class/method counts, class-kind breakdown, endpoint count, edge tallies, schema version. |
| `impact` | `target` (`class`\|`method`\|`step`\|`endpoint`), `value` | Blast radius: affected scenarios (feature + the connecting step text), with step-definition and feature counts. `ImpactAnalyzer.Analyze`. |
| `search_steps` | `query` | Step definitions matching the FTS query (expression + method + class). `MapReader.SearchSteps`. |
| `search_scenarios` | `query` | Scenarios matching the FTS query (feature + scenario + step text + tags). `MapReader.SearchScenarios`. |
| `list_endpoints` | `limit` (optional) | Endpoints/operations, highest scenario-reach first, each with verb, real route (`Path` when known) + request type + API bucket, call sites, and scenario blast radius. |

List responses are capped (`MaxRows`) so a large map can't flood the agent; `impact` reports how many
scenarios were truncated. Deterministic: same map in → same bytes out.

## Later (not in slice 1)

Forward endpoint view per scenario, project-dependency queries (incl. the `references` edges), `resources`
for whole-map export, and streaming progress. Each is additive over the same read-only seam.
