<h1 align="center">🗺️ TestAtlas</h1>

<p align="center">
  <strong>A queryable, semantic map of your .NET test-automation solution — in one SQLite file.</strong>
</p>

<p align="center">
  <em>Zero config&nbsp; ·&nbsp; No AI&nbsp; ·&nbsp; No network&nbsp; ·&nbsp; Deterministic</em>
</p>

<p align="center">
  <img alt="status" src="https://img.shields.io/badge/status-v0.1%20draft-orange">
  <img alt="dotnet" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white">
  <img alt="output" src="https://img.shields.io/badge/output-SQLite-003B57?logo=sqlite&logoColor=white">
  <img alt="mcp" src="https://img.shields.io/badge/MCP-ready-7C3AED">
  <img alt="license" src="https://img.shields.io/badge/license-MIT-blue">
</p>

<p align="center">
  <a href="#-quick-start">Quick start</a> ·
  <a href="#-commands">Commands</a> ·
  <a href="#-use-it-from-an-ai-agent-mcp">MCP</a> ·
  <a href="#-see-it-on-a-real-sample">Sample</a> ·
  <a href="#-keeping-the-map-fresh">Keeping fresh</a> ·
  <a href="#-roadmap">Roadmap</a>
</p>

---

## What it does

TestAtlas statically analyses a .NET test-automation solution and emits a **semantic map** —
`codemap.db`, a single SQLite file — that captures:

- **Projects** and their dependency edges
- **Gherkin** features, scenarios, and steps
- **Step definitions** and their bindings (bound / unbound / ambiguous)
- **Page objects, API clients, helpers, and test classes**
- The **call and usage edges** that connect them all

…and turns that map into precise answers:

| | Capability | What you get |
|:--:|---|---|
| 🔍 | **Search** | FTS5 over step definitions + scenarios — *"does a step for this already exist?"* |
| 💥 | **Impact** | Blast radius — the scenarios affected by changing a class, method, step, or endpoint |
| 📊 | **Report** | A self-contained HTML drill-down of the whole map |
| 🕸️ | **Map** | A self-contained project-dependency graph (HTML) |
| 🔌 | **MCP** | Serve the map to an AI agent over stdio — no context stuffing |
| 📈 | **Stats** | Entity counts, class-kind breakdown, binding coverage, diagnostics |

All of it **offline, deterministic, and reproducible** — same input, same map, every time.

> [!NOTE]
> The indexer spec uses **"CodeMap"** as the working name for the tool/CLI (`testatlas` verb,
> `codemap.db` output). The repository and product are **TestAtlas**.

---

## Why

Large test-automation solutions are hard to navigate — for humans *and* for AI agents. Asked to
automate a new story, an agent can't see where similar code lives, which steps already exist, or
what conventions the solution follows. So it duplicates steps and misplaces code.

TestAtlas indexes the solution **once** and hands both people and agents a structured map to
answer those questions — deterministically, without a model or a network call.

---

## 🚀 Quick start

> **Requires** [.NET SDK 8.0+](https://dotnet.microsoft.com/download)

```bash
# Clone & build
git clone https://github.com/Karzone/TestAtlas.git
cd TestAtlas
dotnet build TestAtlas.sln

# Index a solution → produces ./codemap.db
dotnet run --project src/CodeMap.Cli -- index path/to/YourSolution.sln

# Explore it
dotnet run --project src/CodeMap.Cli -- stats
dotnet run --project src/CodeMap.Cli -- search "login"
dotnet run --project src/CodeMap.Cli -- report        # writes codemap.html
```

Run `index` with no path and TestAtlas auto-discovers a single `.sln`/`.csproj` in the current
directory. The map is written atomically to `./codemap.db`.

### Install as a global tool

The CLI packs as a .NET tool named `testatlas`:

```bash
dotnet pack src/CodeMap.Cli -c Release          # produces nupkg/TestAtlas.Cli.<version>.nupkg
dotnet tool install --global --add-source ./nupkg TestAtlas.Cli
```

…so the workflow becomes just:

```bash
testatlas index path/to/YourSolution.sln
testatlas search "cancel order" --steps
```

---

## 📖 Commands

| Command | What it does |
| --- | --- |
| `index [<path>]` | Analyse a `.sln`/`.csproj` and write the map (default `./codemap.db`). |
| `stats [<db>]` | Entity counts per project, unbound/ambiguous steps, diagnostics. |
| `search [<db>] <query>` | FTS5 full-text search over step definitions and scenarios. |
| `impact [<db>] --class\|--method\|--step\|--endpoint <target>` | Blast radius: scenarios affected by changing an entity. |
| `report [<db>]` | Write a self-contained HTML drill-down of the map. |
| `map [<db>]` | Write a self-contained project dependency graph (HTML). |
| `validate [<db>]` | Check a file is a supported TestAtlas map. |

<details>
<summary><strong>Options &amp; exit codes</strong></summary>

<br>

**`index`** &nbsp;`--output <file>` · `--config <file>` · `--include <glob>` (repeatable) · `--exclude <glob>` (repeatable) · `--verbose` · `--quiet`

**`search`** &nbsp;`--steps` (step definitions only) · `--scenarios` (scenarios only)

**Exit codes** &nbsp;`0` ok · `1` completed with warnings · `2` fatal · `3` bad arguments

Run `testatlas --help` for the full usage text.

</details>

---

## 🧭 Example workflow

```bash
# 1. (Optional) Build the solution — see the note below; the map is the same either way
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

> [!NOTE]
> **You do not need to build the solution before indexing it.** TestAtlas is a syntax-only pass —
> it never asks Roslyn for a compilation or a semantic model — so a solution whose NuGet packages
> have never been restored indexes fine. Gherkin is parsed from the `.feature` files themselves,
> and step definitions are read from the `[Given]`/`[When]`/`[Then]` attributes in your source;
> the code-behind a build generates (`*.feature.cs` under `obj/`) is deliberately **skipped**, so
> building produces nothing the indexer consumes. Build first only if you want the solution
> compiled for your own reasons.

---

## 🔌 Use it from an AI agent (MCP)

TestAtlas ships an MCP server — `testatlas-mcp` — that serves the map to any MCP-aware client
(Claude Code, and other agents) over stdio JSON-RPC. The agent asks a precise question and gets an
exact, structured answer straight from the `.db` — instead of stuffing source files into its
context window.

**Option A — no install** (register the built binary directly):

```bash
dotnet build src/CodeMap.Mcp -c Release
# on Windows the binary is TestAtlas.Mcp.exe; on macOS/Linux it's TestAtlas.Mcp
claude mcp add testatlas -- src/CodeMap.Mcp/bin/Release/net8.0/TestAtlas.Mcp.exe path/to/codemap.db
```

**Option B — install as a global tool** (`testatlas-mcp`), then register that:

```bash
dotnet pack src/CodeMap.Mcp -c Release
dotnet tool install --global --add-source ./nupkg TestAtlas.Mcp
claude mcp add testatlas -- testatlas-mcp path/to/codemap.db
```

Verify it connected, then use it:

```bash
claude mcp list        # the testatlas row should read: ✔ Connected
```

By default the server is registered for the **current project** (`--scope local`). Add
`--scope user` to make it available in every project on your machine, or `--scope project` to
share the registration with your team via a committed `.mcp.json`. A user-scoped registration
pins one `codemap.db`, so per-project registration is usually the better fit across many solutions.

**Tools exposed:** `stats` · `impact` · `search_steps` · `search_scenarios` · `list_endpoints`

> [!IMPORTANT]
> MCP clients load servers **at session start**. If you register the server mid-session, restart
> your Claude Code / agent session before the `testatlas` tools appear.

> [!TIP]
> Retrieval runs locally against the SQLite file — **deterministic, offline, and a few hundred
> tokens per answer** instead of feeding the whole solution to the model. Protocol details in
> [`specs/codemap-mcp.md`](specs/codemap-mcp.md).

---

## 🔎 See it on a real sample

The repo ships a self-contained sample solution — [`samples/SampleShop`](samples/SampleShop) — a
realistic **8-project** layout that mixes **API tests and UI tests**, so the map has plenty of
connected nodes and exercises both API-client mapping (real `HttpClient` clients) and page-object
mapping (real Selenium `IWebDriver` pages):

```text
                       ┌─▶ Api.Catalog  ──┐
Tests.Api  ────────────┼─▶ Api.Cart     ──┤
Tests.E2E  ──┬─────────┴─▶ Api.Identity ──┼─▶ Core   (ApiClientBase : HttpClient)
             └─▶ Ui.Pages ─────────────────┘          (PageBase      : IWebDriver)
Tests.Ui   ────▶ Ui.Pages ──────────────────▶ Core
```

| Project | Role |
|---|---|
| **Core** | base types every client/page inherits (highest in-degree → biggest node) |
| **Api.Catalog / Api.Cart / Api.Identity** | `HttpClient`-based API clients |
| **Ui.Pages** | Selenium page objects (Login / Checkout / Product) |
| **Tests.Api / Tests.Ui / Tests.E2E** | Reqnroll suites whose steps drive the clients and pages |

Two committed outputs, generated by the tool from that solution — the map shows **8 projects and
11 dependencies**:

&nbsp;&nbsp;📊 **[Sample report](https://htmlpreview.github.io/?https://github.com/Karzone/TestAtlas/blob/main/docs/sample-report.html)** — features, scenarios, step bindings, class kinds, API endpoints
<br>
&nbsp;&nbsp;🕸️ **[Sample dependency map](https://htmlpreview.github.io/?https://github.com/Karzone/TestAtlas/blob/main/docs/sample-map.html)** — the eight projects and the edges between them

> [!TIP]
> GitHub serves `.html` as raw source, so the links above route through `htmlpreview.github.io`.
> You can also download [`docs/sample-report.html`](docs/sample-report.html) /
> [`docs/sample-map.html`](docs/sample-map.html) and open them locally.

<details>
<summary>Reproduce them yourself</summary>

<br>

```bash
testatlas index  samples/SampleShop/SampleShop.sln --output sampleshop.db
testatlas report sampleshop.db --html docs/sample-report.html
testatlas map    sampleshop.db --html docs/sample-map.html
```

</details>

---

## 🔄 Keeping the map fresh

TestAtlas answers are deterministic — but only as fresh as the map. Re-index whenever source
changes so a query never returns a *deterministically stale* answer.

### Check whether a map is stale

```bash
python scripts/check-map-age.py [path/to/map.db]   # defaults to ./codemap.db, then ./atlas.db
```

It reads the map's `generated_utc` + `solution_path`, then scans **authored** source
(`*.cs` / `*.feature`) for anything modified since — ignoring generated files
(`*.feature.cs`, `*.g.cs`, `*.designer.cs`), `bin`/`obj`, and any nested solution.

| Exit code | Meaning |
|:--:|---|
| `0` | **fresh** — no source changes since the map was built |
| `1` | **stale** — re-run `testatlas index` |
| `2` | **no map** — nothing to check |

*(Python 3, stdlib only — runs on Windows, macOS, Linux.)*

### Warn automatically after every pull (git hook)

A version-controlled `post-merge` hook runs the check after each merge / `git pull` — it only
prints, never blocks:

```bash
git config core.hooksPath scripts/hooks     # enable once, per clone
```

The hook auto-detects `codemap.db` / `atlas.db` at the repo root, or point it explicitly with
`export TESTATLAS_DB=/path/to/your/map.db`.

### How often to re-index

A full re-index is a single static pass — **seconds** — and its cost scales with **solution size,
not with how much changed**. So re-index on *change*, not on a timer:

- **Locally** — the hook tells you when your map drifted; run `testatlas index` when it does.
- **In CI** — re-index on every merge to your main branch and publish the map as a build artifact
  (don't commit the binary `.db`).

> [!IMPORTANT]
> High churn (many tests added daily) is **not** a performance problem — each pass is a fixed cost,
> so just re-index more often. If a pass ever gets slow on a very large solution, scope it with
> `--include` / `--exclude`.

### Re-index in CI (any provider)

Three steps: **install the tool → run `testatlas index` → publish the `.db` artifact.**

<details>
<summary><strong>GitHub Actions</strong> — <code>.github/workflows/testatlas.yml</code></summary>

<br>

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

</details>

<details>
<summary><strong>Azure DevOps</strong> — <code>azure-pipelines.yml</code></summary>

<br>

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

</details>

Any other CI (GitLab CI, Jenkins, TeamCity, CircleCI) follows the same three steps.

---

## 📁 Project layout

```text
TestAtlas/
├─ src/
│  ├─ CodeMap.Core/     # analysis engine, model, SQLite storage, HTML builders
│  ├─ CodeMap.Cli/      # thin CLI wrapper — packs as the `testatlas` dotnet tool
│  └─ CodeMap.Mcp/      # MCP server — packs as `testatlas-mcp`
├─ tests/
│  ├─ CodeMap.Tests/    # unit / integration tests
│  └─ fixtures/         # synthetic Reqnroll / SpecFlow / broken-solution shims
├─ samples/             # real projects to point the tool at (SampleShop, ReqnrollLoginDemo)
├─ docs/                # committed sample report + dependency map (HTML)
├─ scripts/             # check-map-age.py + git hooks (map freshness / staleness)
├─ specs/               # codemap-indexer.md, codemap-mcp.md — the full specifications
└─ TestAtlas.sln
```

See [`specs/codemap-indexer.md`](specs/codemap-indexer.md) for the complete specification:
entity model, classification heuristics, CLI surface, SQLite schema contract, performance
targets, and acceptance criteria.

---

## 🎯 Design tenets

- **Zero config** — a useful map on an unseen solution, no config file required.
- **Solution agnostic** — heuristic, overridable detection; no company-specific assumptions.
- **Deterministic & offline** — same input ⇒ byte-equivalent logical content; no network, no AI.
- **Graceful degradation** — solutions without Gherkin still yield a useful map.
- **Public schema as contract** — a versioned SQLite schema, so downstream consumers keep working
  even if a third party swaps in their own indexer.

---

## 🗺️ Roadmap

- [x] **Indexer CLI** — C# indexer + documented, versioned SQLite schema *(v0.1)*
- [x] **HTML visualization** — self-contained report + project map generated from the db
- [x] **MCP server** — `testatlas-mcp` exposes the map to AI agents over stdio JSON-RPC
- [ ] **Second-language indexer** — same schema, contract-tested

---

## 📄 License

[MIT](LICENSE) © 2026 Karthik Kalaiyarasu
