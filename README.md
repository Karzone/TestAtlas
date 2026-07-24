<h1 align="center">🗺️ TestAtlas</h1>

<p align="center">
  <strong>A queryable, semantic map of your .NET test-automation solution — in one SQLite file.</strong><br>
  Zero config. No AI. No network. Deterministic output.
</p>

<p align="center">
  <img alt="status" src="https://img.shields.io/badge/status-v0.1%20draft-orange">
  <img alt="dotnet" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white">
  <img alt="license" src="https://img.shields.io/badge/license-MIT-blue">
  <img alt="output" src="https://img.shields.io/badge/output-SQLite-003B57?logo=sqlite&logoColor=white">
</p>

---

## What it does

TestAtlas statically analyses a .NET test-automation solution and emits a **semantic map** —
`codemap.db`, a single SQLite file — describing:

- **Projects** and their dependency edges
- **Gherkin** features, scenarios, and steps
- **Step definitions** and their bindings to steps (bound / unbound / ambiguous)
- **Page objects, API clients, helpers, and test classes**
- The **call and usage edges** that connect them all

From that map you get counts, full-text search, blast-radius ("what breaks if I change this?"),
and self-contained HTML views — all offline and reproducible.

> **Naming note:** the indexer spec uses **"CodeMap"** as the working name for the tool/CLI
> (`testatlas` verb, `codemap.db` output). The repository and product are **TestAtlas**.

---

## Why

Large test-automation solutions are hard to navigate — for humans *and* for AI agents. Asked to
automate a new story, an agent can't see where similar code lives, which steps already exist, or
what conventions the solution follows. So it duplicates steps and misplaces code.

TestAtlas indexes the solution **once** and hands both people and agents a structured map to
answer those questions — deterministically, without a model or a network call.

---

## Requirements

- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)

---

## Quick start

```bash
# Clone
git clone https://github.com/Karzone/TestAtlas.git
cd TestAtlas

# Build
dotnet build TestAtlas.sln

# Index a solution → produces ./codemap.db
dotnet run --project src/CodeMap.Cli -- index path/to/YourSolution.sln

# Explore it
dotnet run --project src/CodeMap.Cli -- stats
dotnet run --project src/CodeMap.Cli -- search "login"
dotnet run --project src/CodeMap.Cli -- report        # writes codemap.html
```

Run `index` with no path and TestAtlas auto-discovers a single `.sln`/`.csproj` in the current
directory. The map is written atomically to `./codemap.db` by default.

### Install as a global tool

The CLI packs as a .NET tool named `testatlas`:

```bash
dotnet pack src/CodeMap.Cli -c Release          # produces nupkg/TestAtlas.Cli.<version>.nupkg
dotnet tool install --global --add-source ./nupkg TestAtlas.Cli
```

Then the workflow becomes just:

```bash
testatlas index path/to/YourSolution.sln
testatlas search "cancel order" --steps
```

---

## Commands

| Command | What it does |
| --- | --- |
| `index [<path>]` | Analyse a `.sln`/`.csproj` and write the map (default `./codemap.db`). |
| `stats [<db>]` | Entity counts per project, unbound/ambiguous steps, diagnostics. |
| `search [<db>] <query>` | FTS5 full-text search over step definitions and scenarios. |
| `impact [<db>] --class\|--method\|--step\|--endpoint <target>` | Blast radius: scenarios affected by changing an entity. |
| `report [<db>]` | Write a self-contained HTML drill-down of the map. |
| `map [<db>]` | Write a self-contained project dependency graph (HTML). |
| `validate [<db>]` | Check a file is a supported TestAtlas map. |

**`index` options:** `--output <file>` · `--config <file>` · `--include <glob>` (repeatable) ·
`--exclude <glob>` (repeatable) · `--verbose` · `--quiet`

**`search` options:** `--steps` (step definitions only) · `--scenarios` (scenarios only)

**Exit codes:** `0` ok · `1` completed with warnings · `2` fatal · `3` bad arguments

Run `testatlas --help` for the full usage text.

---

## Example workflow

```bash
# 1. Build once so any Reqnroll/SpecFlow code-gen produces *.feature.cs
dotnet build YourSolution.sln

# 2. Index it
testatlas index YourSolution.sln --output atlas.db

# 3. Before writing a new step — does one already exist?
testatlas search atlas.db "add a product to the cart" --steps

# 4. About to change a shared client — what will it hit?
testatlas impact atlas.db --class ProductsApiClient

# 5. Share a human-readable snapshot
testatlas report atlas.db --html atlas.html
```

---

## See it on a real sample

The repo ships a self-contained sample solution — [`samples/SampleShop`](samples/SampleShop) — a
realistic **8-project** test-automation layout that mixes **API tests and UI tests**, so the map has
plenty of connected nodes and exercises both API-client mapping (real `HttpClient` clients) and
page-object mapping (real Selenium `IWebDriver` pages):

```
                       ┌─▶ Api.Catalog  ──┐
Tests.Api  ────────────┼─▶ Api.Cart     ──┤
Tests.E2E  ──┬─────────┴─▶ Api.Identity ──┼─▶ Core   (ApiClientBase : HttpClient)
             └─▶ Ui.Pages ─────────────────┘          (PageBase      : IWebDriver)
Tests.Ui   ────▶ Ui.Pages ──────────────────▶ Core
```

- **Core** — base types every client/page inherits (highest in-degree → biggest node)
- **Api.Catalog / Api.Cart / Api.Identity** — `HttpClient`-based API clients
- **Ui.Pages** — Selenium page objects (Login / Checkout / Product)
- **Tests.Api / Tests.Ui / Tests.E2E** — Reqnroll suites whose steps drive the clients and pages

Two committed outputs, generated by the tool from that solution (open in a browser) — the map shows
**8 projects and 11 dependencies**:

- 📊 **[Sample report](https://htmlpreview.github.io/?https://github.com/Karzone/TestAtlas/blob/main/docs/sample-report.html)** — features, scenarios, step bindings, class kinds, API endpoints
- 🕸️ **[Sample dependency map](https://htmlpreview.github.io/?https://github.com/Karzone/TestAtlas/blob/main/docs/sample-map.html)** — the eight projects and the edges between them

> GitHub serves `.html` as raw source, so the links above route through `htmlpreview.github.io`.
> You can also just download [`docs/sample-report.html`](docs/sample-report.html) /
> [`docs/sample-map.html`](docs/sample-map.html) and open them locally.

Reproduce them yourself:

```bash
dotnet build samples/SampleShop/SampleShop.sln
testatlas index  samples/SampleShop/SampleShop.sln --output sampleshop.db
testatlas report sampleshop.db --html docs/sample-report.html
testatlas map    sampleshop.db --html docs/sample-map.html
```

---

## Keeping the map fresh

TestAtlas answers are deterministic — but only as fresh as the map. Re-index whenever source
changes so a query never returns a *deterministically stale* answer.

### Check whether a map is stale

```bash
python scripts/check-map-age.py [path/to/map.db]   # defaults to ./codemap.db, then ./atlas.db
```

It reads the map's `generated_utc` + `solution_path`, then scans **authored** source
(`*.cs` / `*.feature`) for anything modified since — ignoring generated files
(`*.feature.cs`, `*.g.cs`, `*.designer.cs`), `bin`/`obj`, and any nested solution. Exit codes:
`0` fresh · `1` stale · `2` no map — so it drops straight into a hook or CI gate.
(Python 3, stdlib only; runs on Windows, macOS, Linux.)

### Warn automatically after every pull (git hook)

A version-controlled `post-merge` hook runs the check after each merge / `git pull` — it only
prints, never blocks:

```bash
git config core.hooksPath scripts/hooks     # enable once, per clone
```

The hook auto-detects `codemap.db` / `atlas.db` at the repo root, or point it explicitly:

```bash
export TESTATLAS_DB=/path/to/your/map.db
```

### How often to re-index

A full re-index is a single static pass — **seconds**, and its cost scales with **solution size,
not with how much changed**. So re-index on *change*, not on a timer:

- **Locally** — the hook tells you when your map drifted; run `testatlas index` when it does.
- **In CI** — re-index on every merge to your main branch and publish the map as a build
  artifact (don't commit the binary `.db`).

High churn (many tests added daily) is not a performance problem: each pass is a fixed cost, so
just re-index more often. If a pass ever gets slow on a very large solution, scope it with
`--include` / `--exclude`.

### Re-index in CI (any provider)

Three steps: install the tool → run `testatlas index` → publish the `.db` artifact.

**GitHub Actions** — `.github/workflows/testatlas.yml`:

```yaml
name: TestAtlas map
on:
  push:
    branches: [ main ]
jobs:
  map:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet tool install --global TestAtlas.Cli
      - run: testatlas index YourSolution.sln --output codemap.db
      - uses: actions/upload-artifact@v4
        with:
          name: testatlas-map
          path: codemap.db
```

**Azure DevOps** — `azure-pipelines.yml`:

```yaml
trigger:
  branches:
    include: [ main ]
pool:
  vmImage: ubuntu-latest
steps:
  - task: UseDotNet@2
    inputs:
      packageType: sdk
      version: '8.0.x'
  - script: dotnet tool install --global TestAtlas.Cli
    displayName: Install TestAtlas
  - script: testatlas index YourSolution.sln --output codemap.db
    displayName: Index solution
  - publish: codemap.db
    artifact: testatlas-map
```

Any other CI (GitLab CI, Jenkins, TeamCity, CircleCI) follows the same three steps.

---

## Project layout

```
TestAtlas/
├─ src/
│  ├─ CodeMap.Core/     # analysis engine, model, SQLite storage, HTML builders
│  └─ CodeMap.Cli/      # thin CLI wrapper — packs as the `testatlas` dotnet tool
├─ tests/
│  ├─ CodeMap.Tests/    # unit / integration tests
│  └─ fixtures/         # synthetic Reqnroll / SpecFlow / broken-solution shims
├─ samples/             # real projects to point the tool at (SampleShop, ReqnrollLoginDemo)
├─ docs/                # committed sample report + dependency map (HTML)
├─ scripts/             # check-map-age.py + git hooks (map freshness / staleness)
├─ specs/               # codemap-indexer.md — the full specification
└─ TestAtlas.sln
```

See [`specs/codemap-indexer.md`](specs/codemap-indexer.md) for the complete specification:
entity model, classification heuristics, CLI surface, SQLite schema contract, performance
targets, and acceptance criteria.

---

## Design tenets

- **Zero config** — a useful map on an unseen solution, no config file required.
- **Solution agnostic** — heuristic, overridable detection; no company-specific assumptions.
- **Deterministic & offline** — same input ⇒ byte-equivalent logical content; no network, no AI.
- **Graceful degradation** — solutions without Gherkin still yield a useful map.
- **Public schema as contract** — a versioned SQLite schema, so downstream consumers keep working
  even if a third party swaps in their own indexer.

---

## Roadmap

- [x] **Indexer CLI** — C# indexer + documented, versioned SQLite schema *(this repo, v0.1)*
- [x] **HTML visualization** — self-contained report + project map generated from the db (`report` / `map`)
- [x] **MCP server** — `testatlas-mcp` exposes the map to AI agents over stdio JSON-RPC (reads the same db); see `specs/codemap-mcp.md`
- [ ] **Second-language indexer** — same schema, contract-tested

---

## License

[MIT](LICENSE) © 2026 Karthik Kalaiyarasu
