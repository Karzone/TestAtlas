# CodeMap Indexer — Specification (v0.1 draft)

> Component #1 of the CodeMap project: a zero-config CLI that statically analyses a .NET
> test-automation solution and emits a queryable semantic map as a single SQLite file.
> No AI. No network. Deterministic output. "CodeMap" is a working name.

---

## 1. Purpose

Large test-automation solutions become hard to navigate for both humans and AI agents.
When an agent is asked to automate a story, it cannot see where similar code lives, which
steps already exist, or what conventions the solution follows — so it duplicates steps and
places code incorrectly.

The indexer solves the *knowledge* half of that problem. It walks a solution once and
produces a **map file** (`codemap.db`) describing:

- the projects in the solution and what kind of code each contains,
- Gherkin features, scenarios, and their steps (when present),
- step definitions and the scenario steps they bind to,
- page objects, API client classes, helpers, and plain test classes,
- the call/usage edges between all of the above.

Consumers of the map file include (out of scope for this spec, but the map must serve them):

1. An MCP server exposing the map to AI agents (component #2).
2. An HTML visualization for humans.
3. CI checks and ad-hoc SQL queries.

## 2. Goals

- **G1 — Zero config by default.** `codemap index MySolution.sln` must produce a useful map
  on a solution the tool has never seen, with no config file.
- **G2 — Solution agnostic.** No assumptions specific to any one company's structure,
  naming, or folder layout. Detection is heuristic and overridable, never hardcoded.
- **G3 — Deterministic and offline.** Same input ⇒ byte-equivalent logical content.
  No network calls, no telemetry, no AI. Runs on locked-down machines and CI agents.
- **G4 — Graceful degradation.** A solution with no Gherkin at all (plain NUnit/xUnit +
  Playwright/RestSharp) still yields a useful map: the Gherkin layer is optional, not the spine.
- **G5 — Public schema as contract.** The SQLite schema is documented and versioned so that
  third-party indexers (e.g. a future TypeScript indexer) can emit the same schema and reuse
  downstream consumers unchanged.
- **G6 — CI friendly.** Fast enough to rebuild on every merge to main (target: ≤ 3 minutes
  for ~12 projects / ~10,000 tests on a typical build agent), single-file output, meaningful
  exit codes.

## 3. Non-goals (v1)

- **NG1** — No MCP server, no HTML output, no query CLI beyond basic `stats`/`validate`
  (separate components).
- **NG2** — No languages other than C#. (Schema must not preclude them; see G5.)
- **NG3** — No incremental indexing. Full re-index every run.
- **NG4** — No runtime/test-result data (pass/fail, timing, flakiness). The map describes
  code, not executions.
- **NG5** — No semantic embeddings. Search support is lexical (SQLite FTS5).
- **NG6** — No solution modification of any kind. The tool is read-only.

## 4. Supported inputs

| Dimension | v1 support |
|---|---|
| Solution format | `.sln` (given explicitly, or discovered if exactly one exists in cwd); `.csproj` accepted for single-project use |
| Language | C# (Roslyn) |
| BDD frameworks | Reqnroll **and** SpecFlow (attributes from both namespaces) |
| Gherkin | `.feature` files via the Gherkin parser (dialects supported by the parser) |
| Test frameworks | NUnit, xUnit, MSTest (detection only — used to classify test classes/methods) |
| UI automation | Playwright for .NET, Selenium WebDriver (heuristic page-object detection) |
| API automation | RestSharp, raw HttpClient (heuristic client detection) |
| SDK | .NET 8.0+ to run the tool; analysed projects may target any TFM Roslyn can load |

Anything unrecognised is still indexed as generic classes/methods with `kind = other` —
unknown frameworks degrade classification, never break indexing.

## 5. Entity model

Seven entity kinds and one edge table. All entities carry `project_id`, `file_path`,
and line locations.

### 5.1 Entities

- **Project** — name, path, target framework, detected test framework(s), detected
  automation frameworks, and a computed `kind` summary: **any** step class ⇒ `bdd_tests`
  (a project hosting step definitions is BDD test-automation even when generated
  `[TestFixture]`/`[TestClass]` codebehind classes outnumber the step classes — otherwise a
  step-definition project bound to by others is mislabelled `unit_tests`); otherwise test
  classes ⇒ `unit_tests`; otherwise classes present ⇒ `shared_library`; else `other`.
- **Class** — name, namespace, base type, and a classified `kind`:
  `step_class | page_object | api_client | test_class | hook_class | helper | other`.
- **Method** — signature, containing class, visibility, and classified `kind`:
  `step_definition | hook | test_method | page_object_method | api_method | helper_method | other`.
- **StepDefinition** — one row per binding attribute on a method (a method with
  `[Given]` + `[When]` yields two rows): keyword (`Given/When/Then/StepDefinition`),
  expression text, expression kind (`regex | cucumber_expression`), parameter list.
- **Feature** — name, description, tags, source file.
- **Scenario** — name, kind (`scenario | scenario_outline`), tags (own + inherited),
  example-table row count for outlines.
- **ScenarioStep** — ordered steps within a scenario: keyword, text, docstring/table flag.

### 5.2 Edges

A single `edges` table: `(from_kind, from_id, to_kind, to_id, edge_kind, confidence)`.

| edge_kind | From → To | Meaning |
|---|---|---|
| `binds_to` | ScenarioStep → StepDefinition | Step text matches the binding expression. `confidence`: `exact` or `ambiguous` (multiple candidate bindings — all recorded) |
| `unbound` | ScenarioStep → ∅ | Recorded as a diagnostic row; no matching binding found |
| `calls` | Method → Method | Direct invocation found by Roslyn (single hop; consumers can walk the graph for transitive reach) |
| `uses_type` | Method → Class | Method constructs, receives, or dereferences the class (how step classes reach page objects/API clients) |
| `inherits` | Class → Class | Base-type relationship within the solution |

**Keyword-agnostic matching.** A step binds to a definition on **text alone** — the Given/When/Then
keyword is deliberately *not* used to filter candidates, because Reqnroll/SpecFlow themselves ignore
it ("Keywords are not taken into account when looking for a step definition"). A `[When("…")]`
definition therefore binds a `Given` / `Then` / `And` step of the same text. (An earlier build filtered
on keyword and so reported ~86% of real `And` steps as falsely `unbound`; the `BindingKeyword` is now
retained only as metadata.) A consequence: the same text under two different keyword attributes is a
genuine duplicate and surfaces as `ambiguous`, matching SpecFlow's own "no duplicate step text" rule.

**Solution-wide scope.** A feature's steps bind against step definitions in **any** project, not just
their own. Large suites keep step definitions in shared library projects referenced by the feature
projects; scoping per-project reported the majority of real steps as falsely `unbound` (on a 28-project
suite, ~29k of ~55k "unbound" steps had their exact text defined in another project). The trade-off is
that a step text defined in more than one project surfaces as `ambiguous` — which is the honest signal.

**Implementation status.** `binds_to` / `unbound` (slice 2b) and `inherits` / `uses_type` (slice 3)
are built. All are resolved **syntactically** — by simple type name across the solution — to preserve
the "works on unrestored projects" guarantee (G3): `inherits` matches a class's base-type name to a
solution class; `uses_type` matches the type-names a method mentions (parameter/return/`new`/local
types and the types of dereferenced fields/properties) to the **page-object / API-client** classes it
drives, keeping the edge set bounded and signal-rich on large solutions. A name resolving to more than
one class is recorded as `ambiguous`; a name resolving outside the solution produces no edge.
`calls` (Method → Method) is **deferred** — reliable target resolution across overloads needs the
Roslyn semantic model (a restored compilation), which the syntax-only pipeline deliberately avoids.

### 5.3 Full-text search

Two FTS5 virtual tables, populated at index time:

- `search_steps` — over step-definition expression text + method name + class name.
- `search_scenarios` — over feature name + scenario name + step text + tags.

These exist so downstream consumers (MCP server) get indexed lexical search for free.

## 6. Classification heuristics

Heuristics are ordered; first match wins. Every heuristic must be overridable via config (§8).

**Step class** — class carries `[Binding]` (Reqnroll or SpecFlow namespace) or contains ≥ 1
method with a step attribute.

**Page object** — any of, in order:

1. ≥ 50% of instance members reference Playwright (`IPage`, `ILocator`) or Selenium
   (`IWebDriver`, `By`, `IWebElement`) types;
2. class name matches configurable suffix list (default: `Page`, `PageObject`, `Screen`,
   `Component`) **and** references at least one UI-automation type;
3. inherits from a class already classified as a page object.

**API client** — any of, in order:

1. ≥ 50% of methods construct/execute `RestRequest`/`RestClient` or call `HttpClient`
   send methods;
2. name matches suffix list (default: `Client`, `Api`, `Service`, `Endpoint`) **and**
   references RestSharp/HttpClient;
3. inherits from a classified API client.

**Test class** — carries NUnit/xUnit/MSTest test attributes (and is not a step class).

**Hook class / hook method** — `[BeforeScenario]`, `[AfterTestRun]`, etc.

**Helper** — non-empty public surface, referenced (via `calls`/`uses_type`) from ≥ 2
distinct classified classes, and none of the above matched.

**Other** — everything else. Classification failures are silent by design; `other` is a
valid, queryable answer, not an error.

## 7. CLI interface

Distributed as a global/local dotnet tool: `dotnet tool install -g CodeMap.Cli`.

```
codemap index [<path-to-.sln|.csproj>] [options]
    --output <file>        Output path (default: ./codemap.db, overwritten atomically)
    --config <file>        Config file (default: ./codemap.json if present)
    --include <glob>       Project name glob to include (repeatable)
    --exclude <glob>       Project name glob to exclude (repeatable)
    --verbose              Per-project progress and heuristic decisions
    --quiet                Errors only

codemap stats  [<db>]      Human-readable summary: entity counts per project,
                           unbound step count, ambiguous binding count

codemap report [<db>]      Writes a single self-contained HTML drill-down of the map
    --html <file>          Output path (default: <db>.html)

codemap map [<db>]         Writes a self-contained project dependency graph (SVG)
    --html <file>          Output path (default: <db>-map.html)

codemap search [<db>] <query>   FTS5 search over step definitions and scenarios
    --steps                Step definitions only
    --scenarios            Scenarios only

codemap validate [<db>]    Checks file is a CodeMap db and schema version is supported
```

`search` runs the query against both FTS5 indexes: `search_steps` (step-definition expression /
method / class) and `search_scenarios` (feature / scenario / step text / tags), printing each hit
resolved to its name and `file:line`. `--steps` / `--scenarios` narrows to one facet.

The `map` command is a companion visualization: a **project dependency graph**. A directed edge
A→B ("A depends on B") is derived by aggregating the map's cross-project `binds_to` / `uses_type` /
`inherits` edges to project level (an edge whose endpoints resolve to two different projects). Nodes
are laid out on a deterministic circle, sized by in-degree so shared step-definition / page-object
libraries stand out, coloured by project kind, with hover-to-isolate + pan/zoom. **Clicking a project
pins it** and opens a dependency panel listing what it *depends on* and what *depends on it*, each with
the underlying link breakdown (e.g. `→ SharedSteps · 340 binds_to`); clicking a panel entry walks the
chain to that project, and each entry **expands to the class level** — the specific step / page-object
classes behind that dependency, with counts (e.g. `CommonSteps · 320 binds_to`). A manual light/dark toggle (persisted) and a collapsible header (full-viewport
graph) round it out. Self-contained (inline SVG + vanilla JS, no libraries). Only projects and their
cross-project edges are read, so it is instant.

The `report` command is the **v1.x HTML visualization** on the roadmap (§13). It reads a map
file and emits one self-contained HTML document — inline CSS/JS only, no external stylesheet,
script, font, or network request, so it opens offline straight from disk. Sections: summary
counts, step-binding **coverage** (bound / ambiguous / unbound), class-kind breakdown,
per-project table, a feature → scenario → step drill-down where each step is tagged with its
resolved step definition (or the honest "no matching step definition" for `unbound`), and a
diagnostics table, and a **collaborators** panel (page objects / API clients ranked by how many
distinct methods drive them via `uses_type`, with the base class from `inherits` and an **unused**
flag on orphans nothing drives). All map-derived text is HTML-escaped. Deterministic: the only volatile value
(the generated timestamp) is read from the map, not the clock. When the map's `user_version`
predates the current schema, the report shows a **stale-schema banner** (and `report`/`search`
print a matching console note) explaining that facets like Gherkin features / coverage / search are
empty because the map is old — re-run `index` to populate them — so empty sections never read as a bug.

**Exit codes:** `0` success · `1` completed with warnings (e.g. a project failed to load
— map is still written, gaps noted in `diagnostics`) · `2` fatal (no loadable projects,
unwritable output) · `3` bad arguments.

**Console contract:** default output is a short summary (projects indexed, entities found,
unbound steps, elapsed time). All diagnostics also land in the db (§9) so CI does not need
to parse the console.

## 8. Configuration (`codemap.json`, optional)

Everything has a default; the file exists only to override heuristics on unusual codebases.

```json
{
  "$schema": "https://<repo>/schemas/codemap.config.v1.json",
  "exclude": ["**/LegacyTests.csproj"],
  "pageObjectSuffixes": ["Page", "Screen", "Widget"],
  "apiClientSuffixes": ["Client", "Gateway"],
  "classifyOverrides": [
    { "class": "MyCompany.Core.Navigator", "kind": "page_object" }
  ]
}
```

Unknown config keys are a warning, not an error (forward compatibility).

## 9. Output: the map file

- Single SQLite database, schema version stamped via `PRAGMA user_version` (starts at `1`).
- A `meta` table records tool version, timestamp (UTC), absolute solution path, and a
  content hash of the inputs (project files + source files) so consumers can detect staleness.
- A `diagnostics` table records warnings: projects that failed to load, unbound steps,
  ambiguous bindings, files that failed to parse — each with location and message.
- Written atomically: index to a temp file, then rename over the target, so a consumer
  never observes a half-written map.
- Schema documented in `docs/schema.md` in the repo; any breaking change increments
  `user_version` and the changelog states the migration ("re-run `codemap index`").
- Generated build output (`obj/`, `bin/`) is excluded from extraction, so machine-generated
  sources (AssemblyInfo, test-runner hooks) don't pollute the map.

**Schema changelog**
- `user_version = 1` — projects / classes / methods / diagnostics / meta (slice 1).
- `user_version = 2` — adds the `step_definitions` table and populates real class/method `kind`
  values (slice 2a). Migration: re-run `codemap index`.
- `user_version = 3` — adds the Gherkin side of the map (slice 2b): the `features`, `scenarios`,
  and `scenario_steps` tables parsed from `.feature` files; the `edges` table carrying
  `binds_to` (with `confidence` = `exact` | `ambiguous`) and `unbound` step→step-definition
  links resolved by the matcher; and the FTS5 tables `search_steps` (step-definition
  expression / method / class) and `search_scenarios` (feature / scenario / step text / tags).
  Migration: re-run `codemap index`.

## 10. Performance targets

| Scenario | Target |
|---|---|
| Reference solution (~12 projects, ~10k scenarios/tests) | ≤ 3 min cold on a 4-core CI agent |
| Small solution (1–2 projects) | ≤ 15 s |
| Memory | ≤ 2 GB working set on the reference solution |

Determinism requirement: two runs on identical input produce identical row content
(ordering aside) — verified by a test that diffs logical dumps.

## 11. Acceptance criteria

1. **A1 — Own solution:** indexes the author's 12-project solution with zero config;
   spot-check of 20 known bindings shows correct `binds_to` edges; known page objects and
   API clients are classified correctly at ≥ 90%.
2. **A2 — Foreign solution:** indexes at least one public OSS automation repo (never seen
   during development) untouched and with zero config; produces non-empty step/scenario
   entities and a plausible classification breakdown. This is the agnosticism proof.
3. **A3 — No-Gherkin solution:** a plain NUnit + Playwright repo yields classes, methods,
   page objects, and edges, with empty (not erroring) Gherkin tables.
4. **A4 — Both BDD dialects:** one SpecFlow-based and one Reqnroll-based project in the
   same run both index correctly.
5. **A5 — Broken project tolerance:** a solution containing one project that fails to
   compile still produces a map for the remaining projects, exit code `1`, diagnostic recorded.
6. **A6 — Determinism:** repeat-run diff test (§10) passes.
7. **A7 — FTS sanity:** `search_steps` returns the known password-related step definitions
   for the query `password` on the reference solution.

## 12. Open questions

- **Q1** — Should `calls` edges cross project boundaries only, or include intra-class calls?
  (Proposal: include all; consumers filter. Revisit if db size becomes a problem.)
- **Q2** — Scenario Outline examples: index example-table *values* for search, or count
  only? (Proposal: count only in v1; values can be large and low-signal.)
- **Q3** — Multi-solution repos: accept multiple `.sln` inputs into one map, or one map
  per solution? (Proposal: one map per invocation; merging is a v2 concern.)
- **Q4** — Should tag inheritance (`@feature-tag` → scenarios) be materialised or computed
  by consumers? (Proposal: materialise — cheap, and simplifies every consumer.)

## 13. Roadmap context (non-binding)

- **v1** — this spec: C# indexer CLI + documented schema.
- **v1.x** — HTML visualization generated from the db. **Landed** as `testatlas report [<db>] --html`.
- **v2** — MCP server package reading the same db; agent-wiring documentation.
- **Later** — second-language indexer (schema contract test), incremental indexing.
