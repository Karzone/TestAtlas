# Distribution & listings

Where TestAtlas is published, and the status of pending directory/registry submissions.

## Live

| Channel | Identifier | Status |
| --- | --- | --- |
| NuGet — server | [`TestAtlas.Mcp`](https://www.nuget.org/packages/TestAtlas.Mcp) | v0.1.12 |
| NuGet — CLI | [`TestAtlas.Cli`](https://www.nuget.org/packages/TestAtlas.Cli) | v0.1.12 |
| Official MCP Registry | `io.github.Karzone/TestAtlas.Mcp` | v0.1.12, active |
| Anthropic Directory (Claude Code + Cowork) | TestAtlas, by Karzone | Plugin v0.1.1 live since 2026-10-03. v0.1.2 (`/testatlas:report`) pushed 2026-10-04; its directory scan was not checked at the time of writing |
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
| Glama | [glama.ai/mcp/servers/Karzone/TestAtlas](https://glama.ai/mcp/servers/Karzone/TestAtlas) | Release 0.1.11 published 2026-09-26 (0.1.10 on 2026-09-05). GitHub release v0.1.12 made 2026-10-04; the auto-release build's `serverInfo` version was not checked at the time of writing | The first submission failed with an orphaned-duplicate record (resubmitted 2026-07-30); the server was then claimed and a release made. Glama ignores the repo `Dockerfile` and generates its own Debian image from the build spec on `/admin/dockerfile`: build steps install libicu, the .NET 8 SDK (dotnet-install.sh), `TestAtlas.Cli` + `TestAtlas.Mcp` from NuGet, then index `samples/SampleShop` to `/app/codemap.db`; CMD is `/root/.dotnet/tools/testatlas-mcp /app/codemap.db`; no environment variables. Glama **Auto-Release is on** (Releases tab): every GitHub release triggers a Glama build + publish. The build installs whatever NuGet's index serves at that moment, and `publish.yml` itself waits up to 15 min for NuGet, so an auto-build may run before the new version is indexed and ship the previous one — check the build log's `serverInfo` version after each release and rebuild by hand if it is stale. |
| Claude Code plugin marketplace | `claude plugin marketplace add Karzone/TestAtlas` then `claude plugin install testatlas@testatlas` | Live: the repo marketplace, and the Anthropic Directory since 2026-10-03 | The repo is its own marketplace (`.claude-plugin/marketplace.json`); the plugin is `claude-plugin/` (server with no args + skill + `/testatlas:index` + SessionStart map check). Installing clones the whole repo into the plugin cache. Submitted to Anthropic's plugin directory 2026-09-26 (claude.ai/directory/manage, source `Karzone/TestAtlas` @ main, path `claude-plugin`, listed on Claude Code + Cowork only): the first scan refused the commit ("files the scanner won't accept") because `.gitattributes` carried a raw carriage-return byte inside a comment; with that byte removed (11cc824) the scan passed and the version is In review, held on three findings (hook script calls another file, server is a PATH tool not a bundled file, name near the connector "nextatlas"). GitHub push webhook set up 2026-09-26 (repo hook 686233018, push events, JSON; ping delivered 200), so the directory scans a push within minutes. Approved by a reviewer about 2026-09-28 and published 2026-10-03 (v0.1.0, then v0.1.1 the same evening, which says when the two .NET tools are missing). v0.1.2, pushed 2026-10-04, adds `/testatlas:report` and drops the reconnect-after-indexing step, which TestAtlas.Mcp 0.1.12 makes unnecessary. The terminal's `/plugin` Discover tab does not search the Anthropic Directory (the CLI lists it as browse-on-claude.ai only), so terminal users add the repo marketplace; the desktop app and claude.ai find the listing by name. |
| awesome-mcp-servers | — | Unblocked | Was waiting on an installable Glama listing (see row above); PR can be resubmitted. |

## Notes

- The Official MCP Registry is the source of truth; the GitHub MCP Registry is a **separate curated** gallery and does not auto-ingest Official Registry entries — it required the manual nomination (ticket #152789), now approved and live in the VS / VS Code *Browse* gallery.
- Manual install is still possible via `.mcp.json` if needed (pass the map path explicitly — with no
  `codemap.db` in the agent's working dir the bare command has no map, and every tool call says so):
  `{ "servers": { "testatlas": { "type": "stdio", "command": "testatlas-mcp", "args": ["C:\\path\\to\\codemap.db"] } } }`
