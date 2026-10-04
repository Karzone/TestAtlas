---
name: testatlas
description: Use for any question about the tests in a .NET test-automation solution (Reqnroll, SpecFlow, NUnit, xUnit, MSTest, Playwright, RestSharp) - finding, identifying, listing or counting the tests, scenarios or step definitions for a feature or keyword, checking whether coverage exists, and before writing, changing or reviewing tests. Answers through the testatlas MCP tools instead of grep or reading files - existing step definitions, scenarios, tags, page objects and API endpoints - and traces which scenarios a change would affect.
---

# TestAtlas

The `testatlas` MCP server serves a semantic map of the solution at the project root
(`codemap.db`, built by `testatlas index`). Every tool is **read-only and offline**: nothing you
call here changes the code, the map or the network. Ask the map first; only open source files
when the map has sent you to a specific `file:line`.

If a call answers "TestAtlas has no map loaded", the map has not been built for this project. Run
`/testatlas:index` and call the tool again: the server (TestAtlas.Mcp 0.1.12 and later) picks the
map up on the next call. Do not fall back to the `testatlas` command line while the tools are
refusing; build the map and retry. If the retry still says "no map loaded" and the message names no
directory, the server is 0.1.11 or older and read the map only at startup: reconnect it (`/mcp`) or
restart the session, and offer `dotnet tool update --global TestAtlas.Mcp`. If it names a directory
other than the project root, the server was started somewhere else: build the map there or pass it
by path.

For a human-readable overview rather than a lookup, `/testatlas:report` writes the map as one HTML
file.

## If the tools are not installed

The plugin registers the server as `testatlas-mcp` on the PATH but does not install it. When the
`testatlas` server shows as failed in `/mcp`, the session-start note says a tool is missing, or
`command -v testatlas-mcp` finds nothing, stop and offer the install before anything else. Show
the commands exactly like this (they are the same in PowerShell, bash and zsh) and offer to run
them yourself; they need the .NET 8 SDK or later and reach only nuget.org:

```bash
dotnet tool install --global TestAtlas.Cli
```

```bash
dotnet tool install --global TestAtlas.Mcp
```

Then: `/mcp` → reconnect `testatlas` (or restart the session), and `/testatlas:index` to build
the map. Already installed but behind? `dotnet tool update --global TestAtlas.Cli` and the same
for `TestAtlas.Mcp`.

## Which tool, when

| You are about to | Call first |
| --- | --- |
| Write a Gherkin step | `resolve_step` with the phrase (no Given/When/Then). `exact` means reuse that definition verbatim; `ambiguous` is a conflict to resolve before adding anything; `none` returns the closest existing definitions to adapt. |
| Compose a new scenario | `step_catalog` for the reusable vocabulary and its placeholder values, `list_tags` to tag it the way the suite already does. |
| Copy the shape of an existing scenario | `get_scenario` by name substring, then `get_step_definition` for any step you want to reuse or change. |
| Check whether coverage already exists | `search_scenarios` (feature, scenario, step text, tags) or `search_steps` (expression, method, class). |
| Change a class, method, step definition or endpoint | `impact` with `target` + `value`. The result is the list of scenarios that will break; read it before editing. |
| Find what a suite calls over HTTP | `list_endpoints`, highest blast radius first. |
| Understand how the projects depend on each other | `project_dependencies`, optionally filtered by project name. |
| Orient in an unfamiliar suite | `stats` for counts and edge tallies. |

## Rules

0. "Find / identify / list / count the tests for X" is a map question, not a file search. Start with
   `search_scenarios` (and `search_steps` for the steps behind them), try the obvious synonyms as
   separate queries, then `get_scenario` for the ones that matter. Do not grep `.feature` files
   first; grep only to confirm something the map could not answer, and say that you did.
1. Never author a step definition without a `resolve_step` call that returned `none`. Duplicated
   steps are the failure mode this map exists to prevent.
2. Before changing a step definition, page object or API client, run `impact` and name the affected
   scenarios in your answer.
3. Prefer `get_scenario` / `get_step_definition` over reading feature and binding files; they return
   `file:line` when you do need the source.
4. When the session-start note says the map is stale, or you have just changed `.cs` / `.feature`
   files yourself, re-index with `/testatlas:index` before trusting any answer.
5. Results are capped at 200 rows; narrow the query rather than paging.
