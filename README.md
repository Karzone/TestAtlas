<!-- mcp-name: io.github.Karzone/TestAtlas.Mcp -->
<h1 align="center">
  <img src="https://raw.githubusercontent.com/Karzone/TestAtlas/main/assets/logo-mark.svg" width="40" height="40" alt="" valign="middle">
  &nbsp;TestAtlas
</h1>

<p align="center">
  <strong>A queryable, semantic map of your .NET test-automation solution — in one SQLite file, served to your AI agent over MCP.</strong>
</p>

<p align="center">
  <em>Zero config&nbsp; ·&nbsp; No AI&nbsp; ·&nbsp; No network&nbsp; ·&nbsp; Deterministic</em>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/TestAtlas.Mcp"><img alt="TestAtlas.Mcp on NuGet" src="https://img.shields.io/nuget/v/TestAtlas.Mcp?logo=nuget&label=TestAtlas.Mcp&color=004880"></a>
  <a href="https://www.nuget.org/packages/TestAtlas.Cli"><img alt="TestAtlas.Cli on NuGet" src="https://img.shields.io/nuget/v/TestAtlas.Cli?logo=nuget&label=TestAtlas.Cli&color=004880"></a>
  <img alt="Model Context Protocol — listed" src="https://img.shields.io/badge/MCP_Registry-listed-7C3AED">
  <img alt=".NET 8.0" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white">
  <img alt="MIT license" src="https://img.shields.io/badge/license-MIT-blue">
</p>

<p align="center">
  <sub><b>11 MCP tools</b></sub><br>
  <sub><code>resolve_step</code> · <code>step_catalog</code> · <code>impact</code> · <code>search_steps</code> · <code>search_scenarios</code> · <code>get_scenario</code> · <code>get_step_definition</code> · <code>list_tags</code> · <code>list_endpoints</code> · <code>project_dependencies</code> · <code>stats</code></sub>
</p>

<p align="center">
  <a href="#why">Why</a> ·
  <a href="#see-it">See it</a> ·
  <a href="#-quick-start">Quick start</a> ·
  <a href="#-use-it-from-an-ai-agent-mcp">MCP</a> ·
  <a href="#-commands">Commands</a> ·
  <a href="#-keeping-the-map-fresh">Fresh maps</a> ·
  <a href="#-roadmap">Roadmap</a>
</p>

---

## Why

Large test-automation solutions are hard to navigate — for humans *and* for AI agents. Asked to
automate a new story, an agent can't see which steps already exist, where similar code lives, or
what conventions the solution follows — so it duplicates steps and misplaces code. TestAtlas
indexes the solution once into a single SQLite map and answers those questions precisely:
deterministically, offline, without a model or a network call.

---

## See it

Index the bundled 8-project sample once — `testatlas index samples/SampleShop/SampleShop.sln` — then
ask it questions from the terminal. Every number below is real output from that run:

```console
$ testatlas stats sampleshop.db
TestAtlas map: sampleshop.db (schema v5)

totals: 8 project(s), 15 class(es), 36 method(s), 14 step definition(s)
class kinds:
  api_client     7
  page_object    4
  step_class     4

gherkin: 4 feature(s), 5 scenario(s), 16 step(s)
bound steps: 16 · unbound: 0 · ambiguous: 0
endpoints: 3 (3 call site(s))
```

…or let your agent ask them over MCP. Here an agent checks whether a step it is about to write
already exists — `resolve_step` answers with the definitions that would bind it, or the closest
near-misses to reuse instead:

```jsonc
// agent → testatlas: resolve_step { "text": "I add the product to my cart" }
{
  "status": "none",            // nothing binds this exact text — don't invent it from scratch:
  "suggestions": [             // these existing steps are the closest, ranked by shared terms
    { "expression": "product (.*) is added to the cart with quantity (.*)",
      "keyword": "When", "location": "SampleShop.Tests.Api/Steps/CatalogApiSteps.cs:34" },
    { "expression": "the cart is not empty",
      "keyword": "Then", "location": "SampleShop.Tests.Api/Steps/CatalogApiSteps.cs:43" }
    // …8 more
  ]
}
```

Live sample outputs, committed from that same solution:
**[HTML report](https://htmlpreview.github.io/?https://github.com/Karzone/TestAtlas/blob/main/docs/sample-report.html)** (features, scenarios, bindings, class kinds, endpoints) ·
**[dependency map](https://htmlpreview.github.io/?https://github.com/Karzone/TestAtlas/blob/main/docs/sample-map.html)** (the eight projects and their edges).
<sub>(GitHub serves `.html` as source, so the links route through htmlpreview.github.io — or download from [`docs/`](docs/) and open locally.)</sub>

## What you get

TestAtlas statically analyses the solution and emits `codemap.db` — projects and their dependency
edges, Gherkin features/scenarios/steps, step definitions and their bindings (bound / unbound /
ambiguous), page objects, API clients, helpers, test classes, and the call/usage edges connecting
them — then turns that map into answers:

| | Capability | What you get |
|:--:|---|---|
| 🧩 | **Reuse-first authoring** | `resolve_step` — would this phrase bind an existing definition? `step_catalog` — the reusable step vocabulary with placeholders + allowed values |
| 💥 | **Impact** | Blast radius — the scenarios affected by changing a class, method, step, or endpoint |
| 🔍 | **Search** | FTS5 over step definitions + scenarios — *"does a step for this already exist?"* |
| 🔌 | **MCP** | All of it served to an AI agent over stdio — precise answers in a few hundred tokens, no context stuffing |
| 📊 | **Report & map** | Self-contained HTML drill-down of the whole map + project-dependency graph |
| 📈 | **Stats** | Entity counts, class-kind breakdown, binding coverage, diagnostics |

All of it **offline, deterministic, and reproducible** — same input, same map, every time.

---

## 🚀 Quick start

> **Requires** the [.NET SDK 8.0+](https://dotnet.microsoft.com/download). On a corporate machine
> where `dotnet tool install` fails with **401**, see
> [docs/troubleshooting.md](docs/troubleshooting.md).

**1 — Install** the CLI (and the MCP server, if you'll connect an agent):

```bash
dotnet tool install --global TestAtlas.Cli
dotnet tool install --global TestAtlas.Mcp
```

**2 — Index** your solution. This produces the map (`./codemap.db`) that every query, report, and
MCP answer reads — nothing works without it:

```bash
testatlas index path/to/YourSolution.sln
```

No need to build or restore the solution first — indexing is a syntax-only pass, so an unrestored
checkout maps fine. Point `index` at a folder (or nothing) and it auto-discovers a single
`.sln`/`.csproj` there.

**3 — Query it:**

```bash
testatlas stats
testatlas search "login"
testatlas report        # writes codemap.html
testatlas map           # writes codemap-map.html
```

…and to serve it to your AI agent, continue to [MCP setup](#-use-it-from-an-ai-agent-mcp).

<details>
<summary><b>Or run from source</b> (no install)</summary>

```bash
git clone https://github.com/Karzone/TestAtlas.git
cd TestAtlas
dotnet build TestAtlas.sln
dotnet run --project src/CodeMap.Cli -- index path/to/YourSolution.sln
```

</details>

---

## 🔌 Use it from an AI agent (MCP)

TestAtlas ships an MCP server — `testatlas-mcp` — that serves the map to any MCP-aware client
(Visual Studio / VS Code Copilot, Claude Code, and others) over stdio JSON-RPC. The agent asks a
precise question and gets an exact, structured answer straight from the `.db` — instead of
stuffing source files into its context window.

Prerequisites: **both tools installed and a map built** — steps 1–2 of the
[Quick start](#-quick-start). Then register the server:

**Visual Studio / VS Code** (GitHub Copilot agent mode) — add to your `.mcp.json`
(`%USERPROFILE%\.mcp.json` or `<SolutionDir>\.mcp.json`):

```json
{
  "servers": {
    "testatlas": {
      "type": "stdio",
      "command": "testatlas-mcp",
      "args": ["C:\\path\\to\\codemap.db"]
    }
  }
}
```

**Pass the map path explicitly** (as above, or via a `TESTATLAS_DB` env var) — most agents launch
the server from their own working directory, not your solution folder, so relying on auto-discovery
makes the server exit with `code 2`. In Visual Studio you can also use **Tools picker → `+` → Add
custom MCP server** to write this entry for you. On the .NET 10 SDK you can skip the install and
use `"command": "dnx", "args": ["TestAtlas.Mcp", "--yes", "C:\\path\\to\\codemap.db"]` — `dnx`
fetches and runs the server on demand.

**Claude Code:**

```bash
claude mcp add testatlas -- testatlas-mcp path/to/codemap.db
claude mcp list        # the testatlas row should read: ✔ Connected
```

By default the server is registered for the **current project** (`--scope local`). Add
`--scope user` to make it available in every project on your machine, or `--scope project` to
share the registration with your team via a committed `.mcp.json`.

> [!IMPORTANT]
> MCP clients load servers **at session start** — if you register mid-session, restart your agent
> session before the `testatlas` tools appear. To confirm it's actually being used (and not
> silently ignored), run the checks in
> [docs/troubleshooting.md](docs/troubleshooting.md#the-agent-doesnt-seem-to-use-testatlas).

**Tools exposed:**

- `resolve_step` — resolve a Gherkin phrase to the existing step definition(s) that would bind it (regex/cucumber, keyword-agnostic). `exact` / `ambiguous` / `none` (+ near-match suggestions ranked by shared terms). Reuse-first authoring: don't write a step that already exists.
- `step_catalog` — the reusable step vocabulary with extracted placeholders and allowed values (cucumber `{type}`, regex `(a|b)` enums). Compose scenarios from what exists.
- `impact` — blast radius of a change: the scenarios affected by a given class, method, step definition, or endpoint.
- `search_steps` — full-text search over step definitions (expression text + method + class name).
- `search_scenarios` — full-text search over scenarios (feature + scenario name + step text + tags).
- `get_scenario` — full detail of scenario(s) by name: feature, tags, kind, example-row count, and the ordered steps.
- `get_step_definition` — full detail of step definition(s) by expression: keyword, params, C# class/method/signature, and the scenarios that use it.
- `list_tags` — the tag taxonomy with per-tag scenario counts, most-used first — tag new scenarios consistently.
- `list_endpoints` — the HTTP endpoints the suite calls, each with verb, route, and scenario blast radius (highest-reach first).
- `project_dependencies` — the implied project dependency graph (depends-on / depended-on-by), e.g. *"what depends on the Party project?"*.
- `stats` — summary counts: projects, classes, methods, class-kind breakdown, endpoints, and edge tallies.

Retrieval runs locally against the SQLite file — deterministic, offline, and a few hundred tokens
per answer. Protocol details in [`specs/codemap-mcp.md`](specs/codemap-mcp.md); registration from
source and every failure mode in [docs/troubleshooting.md](docs/troubleshooting.md).

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

<details>
<summary><strong>A typical session</strong></summary>

<br>

```bash
# Index the solution (no build needed — the pass is syntax-only)
testatlas index YourSolution.sln --output atlas.db

# Before writing a new step — does one already exist?
testatlas search atlas.db "add a product to the cart" --steps

# About to change a shared client — what will it hit?
testatlas impact atlas.db --class ProductsApiClient

# Share human-readable snapshots
testatlas report atlas.db --html atlas.html
testatlas map    atlas.db --html atlas-map.html
```

</details>

<details>
<summary><strong>About the bundled sample (SampleShop)</strong></summary>

<br>

[`samples/SampleShop`](samples/SampleShop) is a self-contained **8-project** solution mixing API
tests and UI tests, so the map has plenty of connected nodes — real `HttpClient` API clients, real
Selenium `IWebDriver` page objects, and Reqnroll suites driving both:

```text
                       ┌─▶ Api.Catalog  ──┐
Tests.Api  ────────────┼─▶ Api.Cart     ──┤
Tests.E2E  ──┬─────────┴─▶ Api.Identity ──┼─▶ Core   (ApiClientBase : HttpClient)
             └─▶ Ui.Pages ─────────────────┘          (PageBase      : IWebDriver)
Tests.Ui   ────▶ Ui.Pages ──────────────────▶ Core
```

Reproduce the committed sample outputs yourself:

```bash
testatlas index  samples/SampleShop/SampleShop.sln --output sampleshop.db
testatlas report sampleshop.db --html docs/sample-report.html
testatlas map    sampleshop.db --html docs/sample-map.html
```

</details>

---

## 🔄 Keeping the map fresh

Answers are deterministic — but only as fresh as the map, so re-index on *change*, not on a timer.
A full re-index is a single static pass (**seconds**), and its cost scales with solution size, not
with how much changed:

- **Locally** — `python scripts/check-map-age.py` tells you when your map drifted; a
  version-controlled `post-merge` git hook can warn automatically after every pull.
- **In CI (team model)** — re-index on every merge to main and publish the `.db` to a shared feed
  (never commit it — it's a build artifact). Then teammates and agents only need **`TestAtlas.Mcp`**
  locally: download the shared map and point `TESTATLAS_DB` at it — no local `TestAtlas.Cli` or
  indexing required. (Index locally only to include your own *uncommitted* branch work.)

Details — the staleness checker, the git hook, and copy-paste CI recipes (GitHub Actions +
Azure DevOps with a Universal feed) — in **[docs/keeping-the-map-fresh.md](docs/keeping-the-map-fresh.md)**.

---

## 🧹 Uninstall

Two separate things — remove them in this order, so the editor isn't launching a server whose binary just vanished:

1. **Remove the MCP registration** — delete the `testatlas` block from your `.mcp.json`
   (`<SolutionDir>\.mcp.json` or `%USERPROFILE%\.mcp.json`), then restart Visual Studio / VS Code.
   (Or just toggle it off in the agent's tools/wrench picker to keep it for later.)
2. **Uninstall the tools** — they're .NET global tools, not editor add-ins:
   ```bash
   dotnet tool uninstall --global TestAtlas.Mcp
   dotnet tool uninstall --global TestAtlas.Cli
   dotnet tool list --global            # confirm they're gone
   ```
3. **(Optional)** delete the `codemap.db` map file — it's just data, nothing else references it.

---

## 🎯 Design tenets

- **Zero config** — a useful map on an unseen solution, no config file required.
- **Solution agnostic** — heuristic, overridable detection; no company-specific assumptions.
- **Deterministic & offline** — same input ⇒ byte-equivalent logical content; no network, no AI.
- **Graceful degradation** — solutions without Gherkin still yield a useful map.
- **Public schema as contract** — a versioned SQLite schema, so downstream consumers keep working
  even if a third party swaps in their own indexer.

<sub>The project folders use the indexer's working name (**CodeMap**); the shipped tools and
packages are **TestAtlas**. Full specs: [`specs/codemap-indexer.md`](specs/codemap-indexer.md) ·
[`specs/codemap-mcp.md`](specs/codemap-mcp.md). Repo layout: [CONTRIBUTING.md](CONTRIBUTING.md#project-layout).</sub>

---

## 🗺️ Roadmap

- [x] **Indexer CLI** — C# indexer + documented, versioned SQLite schema
- [x] **HTML visualization** — self-contained report + project map generated from the db
- [x] **MCP server** — `testatlas-mcp` exposes the map to AI agents over stdio JSON-RPC (11 tools)
- [ ] **Second-language indexer** — same schema, contract-tested

**Deliberately not planned** (see [design tenets](#-design-tenets)): LLM-assisted analysis inside
the indexer, network calls at index/query time, running or generating tests, and semantic
(compilation-based) analysis that would require a restored build. Releases and per-version notes
live on the [releases page](https://github.com/Karzone/TestAtlas/releases); current distribution
channels in [docs/DISTRIBUTION.md](docs/DISTRIBUTION.md).

---

## 📄 License

[MIT](LICENSE) © 2026 Karthik Kalaiyarasu
