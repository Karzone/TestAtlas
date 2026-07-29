# Distribution & listings

Where TestAtlas is published, and the status of pending directory/registry submissions.

## Live

| Channel | Identifier | Status |
| --- | --- | --- |
| NuGet — server | [`TestAtlas.Mcp`](https://www.nuget.org/packages/TestAtlas.Mcp) | v0.1.6 |
| NuGet — CLI | [`TestAtlas.Cli`](https://www.nuget.org/packages/TestAtlas.Cli) | v0.1.6 |
| Official MCP Registry | `io.github.Karzone/TestAtlas.Mcp` | v0.1.6, active |

## Pending submissions

| Channel | Reference | Status | Notes |
| --- | --- | --- | --- |
| GitHub MCP Registry (VS Code / Visual Studio *Browse* gallery) | GitHub Support ticket **#152789** | Awaiting review | Email nomination to partnerships@github.com; human-reviewed, no SLA. Approval → one-click install in the VS Code / VS MCP gallery. |
| awesome-mcp-servers | — | Blocked | Depends on the Glama listing, which failed with an orphaned-duplicate for this repo. |

## Notes

- The Official MCP Registry is the source of truth; the GitHub MCP Registry is a **separate curated** gallery and does not auto-ingest Official Registry entries — hence the manual nomination above.
- Until the gallery listing lands, add the server in VS / VS Code manually via `.mcp.json` (pass the
  map path explicitly — the bare command exits `code 2` when the agent's working dir has no `codemap.db`):
  `{ "servers": { "testatlas": { "type": "stdio", "command": "testatlas-mcp", "args": ["C:\\path\\to\\codemap.db"] } } }`
