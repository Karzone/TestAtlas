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
- **Endpoint** — an API endpoint referenced by test code: verb + route/operation identity,
  deduplicated solution-wide on (verb, route). Two shapes share the entity, distinguished
  **structurally** by whether the route contains `/` (a C# type name never can):
  - **URL routes** (route contains `/`, e.g. `POST /api/orders/{id}`) — extracted **syntactically**
    and solution-agnostically via a ladder: known client shapes (`HttpClient`'s `GetAsync`/
    `PostAsJsonAsync`/…, `new HttpRequestMessage(HttpMethod.X, …)`, RestSharp's `new RestRequest(…,
    Method.X)` / `Resource = "…"` assignments, Refit-style `[Get("…")]` attributes), a
    **verb-as-argument** tier (any invocation passing `HttpMethod.X` / `Method.X` alongside a
    route-like string — central client wrappers like `ExecuteAsync(HttpMethod.Get, "/x")`), and a
    **generic fallback** for custom wrappers (an invocation named with a verb word —
    `Get/Post/Put/Patch/Delete` — passing a strictly route-like literal: starts with `/`, or contains
    `://` or `/{`). Route arguments may be literals, interpolated strings (holes → `{expr}` template),
    or `const` / `static readonly` string fields of the containing class. **XPath-shaped strings are
    rejected** (leading `//`, `text()`-style calls, `/ns:element` segments) — they are selectors, not
    routes.
  - **Operation-level** (route is a bare type name, no `/`) — for frameworks that hide the URL inside
    a typed request object rather than at the call site (`new BaseRequest<GetUserRequest>()
    .ExecuteAsync()` — the real 1FrameworkAutomatedTest shape). When a **single-type-argument generic
    construction** `new Wrapper<Request>()` uses a `Wrapper` classified `api_client` (see §6), the
    **request type is the operation identity** (route = `GetUserRequest`). **Statically-recovered route
    (slice 5):** when the request type is a **request descriptor** — a class declaring its route as a
    string-literal getter (`ServiceName`/`Resource`/…) and its verb as a getter returning a
    `Method`/`HttpMethod` member — those resolve into the endpoint's `path` (the real route, e.g.
    `api/gateway/estimate/definition/section/list`, a `{0}` template kept verbatim), a **real verb**,
    and `target_api` (the logical API bucket, e.g. `MotorBff`). Detection keys on the universal
    `Method`-returning getter, not any one interface name, so it generalises past 1FAT. When no
    descriptor is found the verb falls back to inference from the leading verb word (`Get…` → GET,
    `Create…`/`Add…`/`Submit…` → POST, `Update…` → PUT, `Delete…`/`Remove…` → DELETE, else `ANY`, never
    a guess) and `path`/`target_api` stay null. The `api_client` gate is the whole
    filter: `new List<Foo>()` is discarded because `List` is not a solution `api_client`. A type
    argument that is a **generic type parameter in scope** (the method's own or an enclosing type's) is
    **not** a request type and is excluded — `new BaseRequest<TRequest>()` inside
    `RequestAsync<TRequest>()` names the parameter, not an operation, so the generic plumbing
    (`BaseApiService`) never surfaces as a phantom endpoint.

  Fully dynamic URLs / unclassified wrappers degrade to nothing, never an error. Call sites become
  `calls_endpoint` edges either way, so `impact --endpoint` and the report's blast radius are uniform.

### 5.2 Edges

A single `edges` table: `(from_kind, from_id, to_kind, to_id, edge_kind, confidence)`.

| edge_kind | From → To | Meaning |
|---|---|---|
| `binds_to` | ScenarioStep → StepDefinition | Step text matches the binding expression. `confidence`: `exact` or `ambiguous` (multiple candidate bindings — all recorded) |
| `unbound` | ScenarioStep → ∅ | Recorded as a diagnostic row; no matching binding found |
| `calls` | Method → Method | Direct invocation found by Roslyn (single hop; consumers can walk the graph for transitive reach) |
| `uses_type` | Method → Class | Method constructs, receives, or dereferences the class (how step classes reach page objects/API clients) |
| `holds` | Class → Class | Class declares the collaborator as a field/property/return/param type — the aggregator/DI shape a name-based `new TypeName()` scan misses (target-typed `new()`, injected fields). The "referenced somewhere" signal, bounded to collaborator targets |
| `inherits` | Class → Class | Base-type relationship within the solution |
| `calls_endpoint` | Method → Endpoint | The method makes an HTTP call to the endpoint (route template) |

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

**Implementation status.** `binds_to` / `unbound` (slice 2b), `inherits` / `uses_type` (slice 3),
and `calls_endpoint` (slice 4) are built. All are resolved **syntactically** — by simple type name across the solution — to preserve
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

**Step class** — class contains ≥ 1 method with a step attribute (`[Given]`/`[When]`/`[Then]`).
`[Binding]` **alone is not sufficient**: it also marks hook-only classes
(`[BeforeScenario]`/`[AfterScenario]`/…), which are **hook classes** — so a shared library that merely
hosts a global hooks class is not promoted to `bdd_tests`.

**Inheritance wins first (both collaborator kinds).** After the step-class check, a class whose base
type is already classified as a page object → page object, or as an API client → API client, **before**
the member-ratio/name heuristics below. An explicit base type is a stronger signal than a member-ratio
guess: a `*ApiService : BaseApiService` that also touches UI types is an API client, not the page object
the greedy UI-ratio rule would otherwise claim.

**Only instantiated types are collaborators.** A `static class` is never a page object or API client
(it holds no state and is never `new`-ed), so it skips every rule below even when it references
RestSharp/UI types — a static RestSharp helper is a helper, not a client.

**Marker types are matched in TYPE positions only.** A UI/API marker (`By`, `IWebDriver`, `HttpClient`,
…) counts as a reference only when the name sits in a genuine type slot — a field/property/parameter/
return/local/base type, a generic type argument, or the type of a `new`/`typeof`/cast/`is`/`as`/
`foreach`. A bare identifier in an expression position (a property or variable merely *named* `By`,
`By = "name"`) does **not** count. Syntax-only (no semantic model, to hold up on unrestored projects),
biased conservative: a static-member access like `By.Id(…)` is not counted — risking a miss, never the
misclassification that a name collision would cause (`By`/`Component` are common ordinary identifiers).

**Page object** — any of, in order:

1. ≥ 50% of instance members reference Playwright (`IPage`, `ILocator`) or Selenium
   (`IWebDriver`, `By`, `IWebElement`) types;
2. class name matches configurable suffix list (default: `Page`, `PageObject`, `Screen`,
   `Component`) **and** references at least one UI-automation type;
3. inherits from a class already classified as a page object (see *inheritance wins first* above).

**API client** — any of, in order:

1. ≥ 50% of methods construct/execute `RestRequest`/`RestClient` or call `HttpClient`
   send methods;
2. **holds or constructs** a RestSharp/HttpClient marker type (a field/property of, or a `new`,
   `RestClient`/`IRestClient`/`RestRequest`/`IRestRequest`/`HttpClient`). This survives the real-world
   shape where the client lives in a **field** driven through a variable, so the marker type name
   never appears in a method *body* — the case rule 1 misses (the 1FAT `BaseRequest<T>` holds a
   concrete `RestClient restClient` field);
3. name matches suffix list (default: `Client`, `Api`, `Service`, `Endpoint`) **and**
   references RestSharp/HttpClient;
4. inherits from a classified API client (handled by *inheritance wins first* above, so it also beats the
   page-object UI-ratio rule);
5. is **named like an API client** (matches the suffix list) **and constructs/wraps** a classified API
   client (`new <api_client>(…)` anywhere in the class body). The name gate is deliberate:
   *composition alone is usage, not identity*. A `*Utilities`/`*Helper`/`*Resolver` that internally
   does `new BaseRequest<T>()` to **call** an operation is a consumer of the API layer, not part of it —
   exactly as a step class that constructs a page object is not a page object (page objects likewise
   propagate only by inheritance, never composition). Without this gate, every utility that reached the
   API layer was mis-promoted: on the real 28-project solution that inflated `api_client` from ~90
   genuine clients to 139 — ~40 false positives, all `*Utilities`/`*Helper`/`*Values`/`*Resolver`.

Rules 4–5 propagate `api_client`-ness through the *named* service layer to a fixpoint, so a chain like
`BaseRequest` (holds `RestClient`) → `BaseApiService` (constructs `BaseRequest`, named `…Service`) →
`*ApiService` (inherits `BaseApiService`) is fully recognised — which is both what makes those services
visible as `uses_type` targets for `impact --class`, and the gate that lets `new BaseRequest<Req>()`
register as an operation-level endpoint (§5.1). Endpoint extraction keys only on the **wrapper** being an
`api_client`, not the caller, so tightening rule 5's precision on the class kind costs **no** endpoint
coverage — a utility that calls `new BaseRequest<Req>()` still registers the call site while itself
staying `other`. Non-solution generic hosts (`List`, `Task`, …) never resolve to a kind, so they never
propagate.

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

codemap impact [<db>]      Blast radius: scenarios affected by changing an entity
    --class <Name>         a page object / API client / step class
    --method <Name>        a specific method
    --step <expr-substr>   step definitions whose expression contains this
    --endpoint <route-substr>  endpoints whose route contains this (API blast radius)

codemap search [<db>] <query>   FTS5 search over step definitions and scenarios
    --steps                Step definitions only
    --scenarios            Scenarios only

codemap validate [<db>]    Checks file is a CodeMap db and schema version is supported
```

`search` runs the query against both FTS5 indexes: `search_steps` (step-definition expression /
method / class) and `search_scenarios` (feature / scenario / step text / tags), printing each hit
resolved to its name and `file:line`. `--steps` / `--scenarios` narrows to one facet.

`impact` is **reverse-dependency analysis** — the edges walked backwards to answer *"if I change this,
which test scenarios could break?"*. From the changed entity it follows `inherits` and `uses_type`
(transitively, through composed page objects) to the step-definition methods that reach it, then
`binds_to` back to the scenario steps and their scenarios / features. Class granularity by design (a
step is affected when its method reaches the changed class), with method-level precision so sibling
step methods in the same class are not swept in. Needs no restored build. For finer "which page-object
*method*" precision the deferred `calls` edge (semantic model) would be required.
`--endpoint` inverts the `calls_endpoint` edges: endpoints matching the route substring → the methods
that call them (directly a step definition, or a client/wrapper method whose class step methods use)
→ the binding scenarios. This is the API-change blast radius — symmetric with the UI page-object case.

The `map` command is a companion visualization: a **project dependency graph**. A directed edge
A→B ("A depends on B") is derived by aggregating the map's cross-project `binds_to` / `uses_type` /
`inherits` edges to project level (an edge whose endpoints resolve to two different projects). Nodes
are laid out on a deterministic circle, sized by in-degree so shared step-definition / page-object
libraries stand out, coloured by project kind, with hover-to-isolate + pan/zoom. **Clicking a project
pins it** and opens a dependency panel listing what it *depends on* and what *depends on it*, each with
the underlying link breakdown (e.g. `→ SharedSteps · 340 binds_to`); clicking a panel entry walks the
chain to that project, and each entry **expands to the class level** — the specific step / page-object
classes behind that dependency, with counts (e.g. `CommonSteps · 320 binds_to`). The pinned panel also
lists the project's **API endpoints** — the routes/operations its methods call (`calls_endpoint`),
with a colour-coded verb badge and an `op` tag on operation-level ones. A manual light/dark toggle
(persisted) and a collapsible header (full-viewport graph) round it out. Self-contained (inline SVG +
vanilla JS, no libraries). Only projects and their cross-project edges (+ endpoints) are read, so it is instant.

The `report` command is the **v1.x HTML visualization** on the roadmap (§13). It reads a map
file and emits one self-contained HTML document — inline CSS/JS only, no external stylesheet,
script, font, or network request, so it opens offline straight from disk. Sections: summary
counts, step-binding **coverage** (bound / ambiguous / unbound), class-kind breakdown,
per-project table, a feature → scenario → step drill-down where each step is tagged with its
resolved step definition (or the honest "no matching step definition" for `unbound`) and each
scenario carries a **forward view** — the endpoints it exercises, as verb-badged chips (the inverse
of the endpoints panel's blast radius: a scenario lists an endpoint iff that endpoint lists the
scenario, since both read the one `EndpointReachAll` pass), and a
diagnostics table, a **collaborators** panel (page objects / API clients ranked by how many
distinct methods drive them via `uses_type`, with the base class from `inherits`; a collaborator is
**used** when a method drives it, something inherits it, *or* a class **holds** it (declares it as a
field/property/return/param type) — an abstract base like `BaseApiService` shows its subclass count and
a held-but-not-directly-driven service shows a `held` tag, neither an unused flag, since inheritance and
the aggregator/DI holding are real use that `uses_type` alone misses (the latter is the target-typed
`new()` shape a name-based scan can't see) — and the genuinely **unused** ones, referenced by nothing at
all, are listed in an expandable section so the count is inspectable rather than opaque; a class reached
only via reflection/`typeof` registries can still be a false positive that no syntax-only pass can see),
and an **API endpoints** panel (routes + operations the suite calls,
each with a colour-coded verb badge, a route/operation kind chip, its call-site count, and its
reverse **blast radius** — how many scenarios reach it, via `EndpointReachAll`). The scenarios count is
an **expandable toggle**: opening a row reveals the reached scenarios grouped by feature, each with the
step text that connects it (via `EndpointScenarioDetails`, resolved only for the shown rows) — the same
drill-down `impact --endpoint` prints, capped per row with a pointer to the CLI for the exhaustive list.
All map-derived text is HTML-escaped. Deterministic: the only volatile value
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
- `user_version = 4` — adds the `endpoints` table and `calls_endpoint` edges (slice 4): the HTTP
  verb + route templates test code calls, tying scenarios to the APIs they exercise. Migration:
  re-run `codemap index`.
- `user_version = 5` — adds nullable `path` + `target_api` columns to `endpoints` (slice 5): an
  operation's statically-recovered real route and API bucket, read from the request type's
  `ServiceName`/`Method` getters. A v4 map has neither column; readers select them only when present,
  so v4 maps still load. Migration: re-run `codemap index`.

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
