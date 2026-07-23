# Samples

Small, real projects used to sanity-check the indexer against genuine framework code
(not the synthetic shims in `tests/fixtures`). These are **not** part of `TestAtlas.sln`
and are not built by CI — they're inputs you point the tool at.

## ReqnrollLoginDemo — a real Reqnroll project

A genuine **Reqnroll.xUnit 2.1.0** project (real package, real `.feature`, real code-gen),
proving the mapper works on authentic Reqnroll code, including Reqnroll's generated
scenario codebehind.

### Run it

```bash
# 1. Build once so Reqnroll's code-gen produces Features/Login.feature.cs
dotnet build samples/ReqnrollLoginDemo/LoginAutomation.sln

# 2. Map it
dotnet run --project src/CodeMap.Cli -- index samples/ReqnrollLoginDemo/LoginAutomation.sln --output reqdemo.db
dotnet run --project src/CodeMap.Cli -- stats reqdemo.db
```

### What the mapper produces (slice 2b)

```
Indexed 1 project(s): 1 class(es), 5 method(s), 5 step definition(s).
gherkin: 1 feature(s), 2 scenario(s), 7 step(s) (6 bound, 1 unbound).
diagnostics: 0 (0 error(s), 0 warning(s)).

class kinds:
  step_class  1
```

- **`LoginStepDefinitions`** → classified **`step_class`**, with its five real `[Given]/[When]/[Then]`
  cucumber-expression **step definitions** extracted (keyword + expression + `expression_kind`).
- **`Login.feature`** is parsed into one **feature**, two **scenarios**, and seven **scenario steps**,
  persisted in the `features` / `scenarios` / `scenario_steps` tables.
- Reqnroll's generated codebehind for `Login.feature` (`LoginFeature` / `FixtureData`) lives under
  `obj/` and is **excluded** — the map indexes source, not generated artefacts.

### Matcher resolution — now persisted as `edges` (slice 2b)

`Login.feature`'s seven steps resolve to six `binds_to` (`exact`) edges on `LoginStepDefinitions` and one
`unbound` edge (`And an unbound narrative step with no binding`). These now live in the `edges` table
(`from` scenario step → `to` step definition, with `edge_kind` and `confidence`); `testatlas stats`
reports the bound / unbound / ambiguous tallies. Steps are searchable via the FTS5 tables
`search_steps` (step-definition text) and `search_scenarios` (feature / scenario / step text / tags).

### Visual report

```bash
dotnet run --project src/CodeMap.Cli -- report reqdemo.db --html reqdemo.html
# -> open reqdemo.html in any browser
```

`testatlas report` writes a single self-contained HTML file (inline CSS/JS, no network) with the
summary counts, a step-binding **coverage** bar, the class-kind breakdown, a per-project table, and a
feature → scenario → step drill-down where each step is colour-tagged **bound** (with its resolved
step definition + `file:line`), **ambiguous** (all candidates listed), or **unbound**. A filter box
narrows the tree client-side. It reads the map only — no solution load — so it's instant and works on
any `codemap.db` you already have.

### Search

```bash
dotnet run --project src/CodeMap.Cli -- search reqdemo.db dashboard
# step definitions matching 'dashboard': 1
#   [Then] the dashboard is shown  (LoginStepDefinitions, LoginStepDefinitions.cs:…)
# scenarios matching 'dashboard': 1
#   Login › Successful sign in  (Login.feature:…)
```

`testatlas search [<db>] <query>` runs the query against both FTS5 indexes — `search_steps`
(step-definition text) and `search_scenarios` (feature / scenario / step text / tags) — and prints
each hit resolved to its name and `file:line`. `--steps` / `--scenarios` narrows to one facet.

### Project dependency map

```bash
dotnet run --project src/CodeMap.Cli -- map reqdemo.db --html reqdemo-map.html
# -> open reqdemo-map.html in any browser
```

`testatlas map [<db>]` renders a self-contained project dependency graph: each project is a node
(sized by how many others depend on it, so shared step / page-object libraries stand out), and a
directed edge A→B means project A binds to / uses / inherits something in project B. Hover a node to
isolate its links; drag to pan, scroll to zoom.

### Impact (blast radius)

```bash
dotnet run --project src/CodeMap.Cli -- impact reqdemo.db --class LoginPage
dotnet run --project src/CodeMap.Cli -- impact reqdemo.db --step "user logs in"
```

`testatlas impact` answers *"if I change this, which test scenarios could break?"* — it walks the
`binds_to` / `uses_type` / `inherits` edges backwards from a page object / API client / step class /
step definition to the affected scenarios and features. Great before a refactor or when a step /
page object is flaky.

### Endpoints (API blast radius)

```bash
dotnet run --project src/CodeMap.Cli -- impact reqdemo.db --endpoint "/api/orders"
```

Slice 4 extracts the **API endpoints** test code calls and ties them to scenarios through
`calls_endpoint` edges. Two shapes are captured: **URL routes** (verb + route template — HttpClient,
RestSharp, Refit, or any custom wrapper via the generic verb-name fallback) and, for frameworks that
hide the URL inside a typed request object, **operation-level** endpoints (`new BaseRequest<GetUserRequest>()`
→ the request type is the operation identity, keyed by name with an inferred verb). `impact --endpoint
<route-or-request>` answers "which scenarios break if this API changes?" — the API-test symmetric of
the page-object case. The `report` and `map` views surface these endpoints and their blast radius too.

> The same commands work on **any** local `.sln`/`.csproj` — e.g. a solution on your own machine that
> this cloud sandbox can't reach.
