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

> The same commands work on **any** local `.sln`/`.csproj` — e.g. a solution on your own machine that
> this cloud sandbox can't reach.
