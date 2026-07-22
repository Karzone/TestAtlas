# TestAtlas

Zero-config CLI that statically analyses a .NET test-automation solution and emits a
queryable **semantic map** as a single SQLite file. No AI. No network. Deterministic output.

> The indexer specification uses **"CodeMap"** as the working name for the tool/CLI; the
> repository and project are **TestAtlas**.

## Why

Large test-automation solutions are hard to navigate for both humans and AI agents. When an
agent is asked to automate a story, it can't see where similar code lives, which steps already
exist, or what conventions the solution follows — so it duplicates steps and misplaces code.
TestAtlas indexes the solution once and produces a map (`codemap.db`) describing projects,
Gherkin features/scenarios/steps, step definitions and their bindings, page objects, API
clients, helpers, test classes, and the call/usage edges between them.

## Status

v0.1 draft — see [`specs/codemap-indexer.md`](specs/codemap-indexer.md) for the full
specification (entity model, classification heuristics, CLI, SQLite schema contract,
performance targets, and acceptance criteria).

## Components (roadmap)

1. **Indexer CLI** — this spec: C# indexer + documented, versioned SQLite schema.
2. **MCP server** — exposes the map to AI agents (reads the same db).
3. **HTML visualization** — human-facing view generated from the db.

## Design tenets

- **Zero config** — useful map on an unseen solution with no config file.
- **Solution agnostic** — heuristic, overridable detection; no company-specific assumptions.
- **Deterministic & offline** — same input ⇒ byte-equivalent logical content; no network, no AI.
- **Graceful degradation** — no-Gherkin solutions still yield a useful map.
- **Public schema as contract** — versioned SQLite schema so third-party indexers can reuse
  downstream consumers unchanged.
