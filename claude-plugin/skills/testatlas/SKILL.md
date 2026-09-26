---
name: testatlas
description: Use before writing, changing or reviewing tests in a .NET test-automation solution (Reqnroll, SpecFlow, NUnit, xUnit, MSTest, Playwright, RestSharp). Looks up existing step definitions, scenarios, page objects and API endpoints through the testatlas MCP tools instead of reading files, and traces which scenarios a change would affect.
---

# TestAtlas

The `testatlas` MCP server serves a semantic map of the solution at the project root
(`codemap.db`, built by `testatlas index`). Every tool is **read-only and offline**: nothing you
call here changes the code, the map or the network. Ask the map first; only open source files
when the map has sent you to a specific `file:line`.

If the tools are missing or a call says there is no map, the map has not been built for this
project. Run `/testatlas:index`, then reconnect the server (`/mcp`) or restart the session.

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

1. Never author a step definition without a `resolve_step` call that returned `none`. Duplicated
   steps are the failure mode this map exists to prevent.
2. Before changing a step definition, page object or API client, run `impact` and name the affected
   scenarios in your answer.
3. Prefer `get_scenario` / `get_step_definition` over reading feature and binding files; they return
   `file:line` when you do need the source.
4. When the session-start note says the map is stale, or you have just changed `.cs` / `.feature`
   files yourself, re-index with `/testatlas:index` before trusting any answer.
5. Results are capped at 200 rows; narrow the query rather than paging.
