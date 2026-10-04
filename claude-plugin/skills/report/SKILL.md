---
name: report
description: Write the TestAtlas HTML report for the .NET solution in this project - a self-contained drill-down of features, scenarios, step bindings, class kinds and endpoints - and optionally the project dependency graph. Use when the person asks for the TestAtlas report, an HTML view or overview of the test suite, a drill-down of the map, or the dependency graph or map of the projects.
---

# Write the TestAtlas report

The report is one self-contained HTML file written from the map (`codemap.db`) by the `testatlas`
command-line tool. It is not an MCP tool: run it in the shell. Nothing is uploaded and nothing but
the HTML file is written.

0. Check the tools are there: `command -v testatlas testatlas-mcp` (PowerShell:
   `Get-Command testatlas, testatlas-mcp`). If either is missing, offer these first and run them
   when the person agrees (they need the .NET 8 SDK or later and reach only nuget.org):

   ```bash
   dotnet tool install --global TestAtlas.Cli
   ```

   ```bash
   dotnet tool install --global TestAtlas.Mcp
   ```

1. Make sure there is a map to report on. If there is no `codemap.db` at the project root, or the
   session-start note said the map is stale, or `.cs` / `.feature` files have changed since it was
   built, build it first with `/testatlas:index`. Never run the report against a missing map.
2. From the project root run:

   ```bash
   testatlas report codemap.db --html codemap.html
   ```

   Exit code 0 is a clean run and 1 is a run that completed with warnings; in both the file was
   written. Relay any warnings. 2 is a failure: show the message.
3. If the person asked for the dependency graph or the project map, or for "everything", also run:

   ```bash
   testatlas map codemap.db --html codemap-map.html
   ```

4. Say where each file was written, as a full path, and offer to open it in the browser. Do not open
   it unasked. To write somewhere else, pass another path to `--html`.

Do not commit the HTML files unless the repository already tracks them; like `codemap.db` they are
build artefacts and go stale with the next change.
