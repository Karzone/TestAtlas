# Distribution & listings

Where TestAtlas is published, and the status of pending directory/registry submissions.

## Live

| Channel | Identifier | Status |
| --- | --- | --- |
| NuGet — server | [`TestAtlas.Mcp`](https://www.nuget.org/packages/TestAtlas.Mcp) | v0.1.10 |
| NuGet — CLI | [`TestAtlas.Cli`](https://www.nuget.org/packages/TestAtlas.Cli) | v0.1.10 |
| Official MCP Registry | `io.github.Karzone/TestAtlas.Mcp` | v0.1.10, active |
| GitHub MCP Registry (VS Code / Visual Studio *Browse* gallery) | Karzone Test Atlas | Listed 2026-07-30 (ticket #152789 approved); one-click Install live |

## Articles & posts

| Channel | Title | URL | Status |
| --- | --- | --- | --- |
| Medium | The blind spot in AI-driven test automation | [medium.com/@karthikawaiting](https://medium.com/@karthikawaiting/the-blind-spot-in-ai-driven-test-automation-the-agent-cant-see-your-existing-tests-so-it-559e2f19d002) | Published 2026-07-27; tags: Test Automation, Software Testing, Dotnet, AI, MCP |
| dev.to | The blind spot in AI-driven test automation | [dev.to/karzone](https://dev.to/karzone/the-blind-spot-in-ai-driven-test-automation-the-agent-cant-see-your-existing-tests-2381) | Published 2026-07-27 (cross-post) |

## Community posts

| Forum | Reference | URL | Status |
| --- | --- | --- | --- |
| Reqnroll GitHub Discussions | Show and tell #1104 | [reqnroll/discussions/1104](https://github.com/orgs/reqnroll/discussions/1104) | Posted 2026-07-30 |

## Pending submissions

| Channel | Reference | Status | Notes |
| --- | --- | --- | --- |
| Glama | [glama.ai/mcp/servers/Karzone/TestAtlas](https://glama.ai/mcp/servers/Karzone/TestAtlas) | Release 0.1.10 published 2026-09-05 | The first submission failed with an orphaned-duplicate record (resubmitted 2026-07-30); the server was then claimed and a release made. Glama ignores the repo `Dockerfile` and generates its own Debian image from the build spec on `/admin/dockerfile`: build steps install libicu, the .NET 8 SDK (dotnet-install.sh), `TestAtlas.Cli` + `TestAtlas.Mcp` from NuGet, then index `samples/SampleShop` to `/app/codemap.db`; CMD is `/root/.dotnet/tools/testatlas-mcp /app/codemap.db`; no environment variables. A new NuGet version needs a new build + release there. |
| awesome-mcp-servers | — | Unblocked | Was waiting on an installable Glama listing (see row above); PR can be resubmitted. |

## Notes

- The Official MCP Registry is the source of truth; the GitHub MCP Registry is a **separate curated** gallery and does not auto-ingest Official Registry entries — it required the manual nomination (ticket #152789), now approved and live in the VS / VS Code *Browse* gallery.
- Manual install is still possible via `.mcp.json` if needed (pass the map path explicitly — the bare
  command exits `code 2` when the agent's working dir has no `codemap.db`):
  `{ "servers": { "testatlas": { "type": "stdio", "command": "testatlas-mcp", "args": ["C:\\path\\to\\codemap.db"] } } }`
