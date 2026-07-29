# Troubleshooting

Fixes for the failure modes people actually hit, in the order they usually hit them.

## `dotnet tool install` fails with `401 Unauthorized` on a corporate machine

If your machine has a private, authenticated NuGet feed configured (e.g. Azure DevOps Artifacts),
`dotnet` queries **every** registered source during install and fails with **401** on the private
one — even though TestAtlas is published on public nuget.org.

Install using only nuget.org. Create a minimal `public.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

…then point the install at it:

```bash
dotnet tool install --global TestAtlas.Cli --configfile ./public.config
dotnet tool install --global TestAtlas.Mcp --configfile ./public.config
```

The `<clear />` drops every inherited source (including the private feed), so `dotnet` only sees
nuget.org. It's a one-off override — it doesn't change your machine's NuGet configuration or affect
the private feed for your other projects.

## The MCP server exits with `code 2` at startup

The server needs a map. It resolves one in this order: a **path argument**, the `TESTATLAS_DB`
environment variable, or a `codemap.db`/`atlas.db` in the current working directory. Most agents
launch the server from **their own** working directory — not your solution folder — so
auto-discovery finds nothing and the server exits with `code 2`.

**Fix: pass the map path explicitly** as the last argument in your `.mcp.json` /
`claude mcp add` registration (double-backslash it on Windows), or set `TESTATLAS_DB`.

## The agent doesn't seem to use TestAtlas

Don't ask the agent *"are you using testatlas?"* — models don't reliably introspect their own tool
calls, and a confident "no" (or a suggestion to "create an index" — that's the editor's own
workspace index, unrelated to TestAtlas) proves nothing. Check these signals instead:

1. **Is the server connected?** Open the **tools/wrench menu** in the chat input — `testatlas` and
   its tools (`stats`, `impact`, `resolve_step`, …) should be listed and enabled. If they're
   missing, the server didn't start — see the `code 2` fix above.
2. **Force a call with a ground-truth question** (in your agent's **Agent** mode, not a plain
   chat/ask mode — only agent mode invokes tools):
   > `Using #testatlas, call the stats tool — how many classes and methods does <a project in your map> have?`

   A correct, specific count the model couldn't have guessed = it queried your map. A vague
   answer = it didn't.
3. **Watch for the tool call itself** — the agent renders a tool-invocation card
   (e.g. `testatlas › stats`) you can expand to see the exact arguments and raw result.

## The tools don't appear after registering the server

MCP clients load servers **at session start**. If you register the server mid-session, restart your
Claude Code / agent session before the `testatlas` tools appear.

## The first tool call pops a consent prompt

The first call usually pops a **client consent prompt** ("This tool is from 'testatlas'…"). That's
the editor's standard MCP safety gate — it fires for *every* MCP server, not just this one.
TestAtlas is local and read-only, so it's safe to **Allow** / **Always allow**.
