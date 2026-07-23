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
- [ ] **MCP server** — expose the map to AI agents (reads the same db)
- [ ] **HTML visualization** — richer human-facing views generated from the db

---

## License

[MIT](LICENSE) © 2026 Karthik Kalaiyarasu
