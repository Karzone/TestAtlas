---
name: index
description: Build or rebuild the TestAtlas map (codemap.db) for the .NET solution in this project. Use when the session-start note says the map is missing or stale, when the testatlas tools report no map, or after editing .cs or .feature files.
---

# Build the TestAtlas map

The map is one SQLite file, `codemap.db`, at the project root. Building it is a syntax-only pass:
no restore, no compilation, no network, seconds on most solutions.

0. Check the tools are there: `command -v testatlas testatlas-mcp` (PowerShell:
   `Get-Command testatlas, testatlas-mcp`). If either is missing, offer these first and run them
   when the person agrees (they need the .NET 8 SDK or later and reach only nuget.org):

   ```bash
   dotnet tool install --global TestAtlas.Cli
   ```

   ```bash
   dotnet tool install --global TestAtlas.Mcp
   ```

1. Find the solution file. If there is exactly one `*.sln` at the project root, use it. If there are
   several, ask which one; do not guess.
2. From the project root run:

   ```bash
   testatlas index <Solution>.sln --output codemap.db
   ```

   The summary line reports projects, classes, methods, step definitions, features, scenarios and
   any diagnostics. Diagnostics are files the indexer could not parse; report them, they are not
   fatal.
3. Check the server sees the new map: call the `stats` tool. TestAtlas.Mcp 0.1.12 and later looks
   for the map on every call and re-reads a rebuilt one, so this answers with the counts just
   printed. If it still answers "no map loaded", look at whether the message names a directory:
   - It names no directory: the server is 0.1.11 or older and read the map only at startup.
     `/mcp` → reconnect `testatlas` (or restart the session) loads the map now; offer
     `dotnet tool update --global TestAtlas.Mcp` so it does not happen again.
   - It names a directory other than this project root: the server was started somewhere else, and
     the map must be built there or passed to it by path.
4. Offer the HTML drill-down of what was just indexed: `/testatlas:report`.

Do not commit `codemap.db` unless the repository already tracks one; it is a build artefact and
the repo's `.gitignore` usually excludes `*.db`.
