# TestAtlas for Claude Code

A Claude Code plugin that gives Claude a queryable map of your .NET test-automation solution:
which step definitions already exist, which scenarios a change would break, which endpoints the
suite calls. Read-only, offline, deterministic. The map is one SQLite file (`codemap.db`) built by
`testatlas index`.

## Install

Both TestAtlas tools must be on the PATH (they need the .NET 8 SDK or later):

```bash
dotnet tool install --global TestAtlas.Cli
dotnet tool install --global TestAtlas.Mcp
```

Then add the marketplace and install the plugin:

```bash
claude plugin marketplace add Karzone/TestAtlas
claude plugin install testatlas@testatlas
```

Open Claude Code at the root of a solution and run `/testatlas:index` once. From then on the
`testatlas` tools answer from the map, and every session opens with a note saying whether the map is
fresh, stale, or missing.

## What is inside

| Component | What it does |
| --- | --- |
| MCP server `testatlas` | `testatlas-mcp` with no arguments; it serves the `codemap.db` at the project root. 11 read-only tools: `resolve_step`, `step_catalog`, `impact`, `search_steps`, `search_scenarios`, `get_scenario`, `get_step_definition`, `list_tags`, `list_endpoints`, `project_dependencies`, `stats`. |
| Skill `testatlas` | Tells Claude which tool to call before writing a step, composing a scenario, or changing a class, step or endpoint. |
| Skill `index` (`/testatlas:index`) | Builds or rebuilds the map for the solution in the current project. |
| `SessionStart` hook | In a project with a `*.sln`, reports whether the map is missing, fresh or stale. Silent elsewhere. Always exits 0. |

## Notes

- The server reads the map once at startup. After building or rebuilding it, reconnect the server
  from `/mcp` or restart the session.
- A project with no `codemap.db` makes the server exit at startup; `/mcp` shows it as failed until
  `/testatlas:index` has run and the server is reconnected.
- `scripts/check-map-age.py` here is a copy of the repository's canonical script; a test in the main
  solution fails if the two drift.

Full documentation: [github.com/Karzone/TestAtlas](https://github.com/Karzone/TestAtlas).
