---
name: index
description: Build or rebuild the TestAtlas map (codemap.db) for the .NET solution in this project. Use when the session-start note says the map is missing or stale, when the testatlas tools report no map, or after editing .cs or .feature files.
---

# Build the TestAtlas map

The map is one SQLite file, `codemap.db`, at the project root. Building it is a syntax-only pass:
no restore, no compilation, no network, seconds on most solutions.

1. Find the solution file. If there is exactly one `*.sln` at the project root, use it. If there are
   several, ask which one; do not guess.
2. From the project root run:

   ```bash
   testatlas index <Solution>.sln --output codemap.db
   ```

   The summary line reports projects, classes, methods, step definitions, features, scenarios and
   any diagnostics. Diagnostics are files the indexer could not parse; report them, they are not
   fatal.
3. If `testatlas` is not on the PATH, install both tools (they need the .NET 8 SDK or later):

   ```bash
   dotnet tool install --global TestAtlas.Cli
   dotnet tool install --global TestAtlas.Mcp
   ```

4. The MCP server reads the map once at startup. A server that started before the map existed is
   still running but holds no map, and a rebuilt map is not picked up either: run `/mcp` and
   reconnect `testatlas`, or restart the session, before using the tools.

Do not commit `codemap.db` unless the repository already tracks one; it is a build artefact and
the repo's `.gitignore` usually excludes `*.db`.
