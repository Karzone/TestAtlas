# TestAtlas for Claude Code

A Claude Code plugin that gives Claude a queryable map of your .NET test-automation solution:
which step definitions already exist, which scenarios a change would break, which endpoints the
suite calls. Read-only, offline, deterministic. The map is one SQLite file (`codemap.db`) built by
`testatlas index`.

## Install

In the Claude desktop app or on claude.ai, find **TestAtlas** in the Anthropic Directory and enable
it there.

In a terminal, the `/plugin` Discover tab searches only the marketplaces you have added, and the
Anthropic Directory is not one of them. Add this repository as a marketplace, then install:

```bash
claude plugin marketplace add Karzone/TestAtlas
claude plugin install testatlas@testatlas
```

Once the marketplace is added, `/plugin` → Discover → **testatlas** finds it too.

The plugin does not install the two TestAtlas tools it drives; they are .NET global tools and need
the .NET 8 SDK or later. If they are missing, the session-start note and the `/testatlas` skill say
so and offer the commands (identical in PowerShell, bash and zsh), and Claude can run them for you:

```bash
dotnet tool install --global TestAtlas.Cli
dotnet tool install --global TestAtlas.Mcp
```

After installing them, reconnect the `testatlas` server from `/mcp` (it failed to start while the
command was missing) or restart the session.

Open Claude Code at the root of a solution and run `/testatlas:index` once. From then on the
`testatlas` tools answer from the map, and every session opens with a note saying whether the map is
fresh, stale, or missing. `/testatlas:report` writes the map as one HTML file you can open in a
browser.

## What is inside

| Component | What it does |
| --- | --- |
| MCP server `testatlas` | `testatlas-mcp` with no arguments; it serves the `codemap.db` at the project root. 11 read-only tools: `resolve_step`, `step_catalog`, `impact`, `search_steps`, `search_scenarios`, `get_scenario`, `get_step_definition`, `list_tags`, `list_endpoints`, `project_dependencies`, `stats`. |
| Skill `testatlas` | Tells Claude which tool to call before writing a step, composing a scenario, or changing a class, step or endpoint. |
| Skill `index` (`/testatlas:index`) | Builds or rebuilds the map for the solution in the current project. |
| Skill `report` (`/testatlas:report`) | Writes `codemap.html`, a self-contained drill-down of features, scenarios, bindings, class kinds and endpoints, and on request `codemap-map.html`, the project dependency graph. Builds the map first if it is missing or stale. |
| `SessionStart` hook | In a project with a `*.sln`, reports whether the map is missing, fresh or stale. Silent elsewhere. Always exits 0. |

## Notes

- The server is part of the plugin: installing the plugin registers it, and it starts with every
  session. It usually starts before the map exists. TestAtlas.Mcp 0.1.12 and later looks for
  `codemap.db` on every call and re-reads a rebuilt one, so the tools answer as soon as
  `/testatlas:index` has run, with nothing to reconnect.
- On TestAtlas.Mcp 0.1.11 the server reads the map once at startup: after building or rebuilding
  it, reconnect the server from `/mcp` or restart the session. Better, update it:
  `dotnet tool update --global TestAtlas.Mcp`.
- `scripts/check-map-age.py` here is a copy of the repository's canonical script; a test in the main
  solution fails if the two drift.

Full documentation: [github.com/Karzone/TestAtlas](https://github.com/Karzone/TestAtlas).
