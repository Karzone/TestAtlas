# Demo walkthrough

The walkthrough video in the main README is generated automatically — no manual
screen recording. It stitches three segments, all produced from the real tool
against [`samples/SampleShop`](../../samples/SampleShop):

1. **Generate** — [`intro.tape`](intro.tape) (a [VHS](https://github.com/charmbracelet/vhs) terminal cast) runs `testatlas index` and renders the HTML report + dependency map.
2. **Explore** — [`screencast.mjs`](screencast.mjs) drives a real browser ([Playwright](https://playwright.dev)) through `report.html` and `map.html`: expanding API endpoints into the scenarios that hit them, filtering the feature tree, and clicking a project node to light up its dependencies.
3. **Ask it like an agent** — [`mcp.tape`](mcp.tape) uses [`mcp-ask.mjs`](mcp-ask.mjs), a tiny example MCP client, to query the map over stdio JSON-RPC — the same thing a coding agent does under the hood.

[`.github/workflows/demo.yml`](../../.github/workflows/demo.yml) renders each
segment on an Ubuntu runner, concatenates them with ffmpeg into
`testatlas-demo.mp4`, grabs a `demo-poster.png`, and commits both back. Because
every segment runs the real CLI/MCP against a real sample, the demo can never
drift from what the tool actually does.

## Showing the video inline on GitHub

The README links a poster image to `testatlas-demo.mp4`. GitHub plays a
repo-committed `.mp4` only as a click-through, **not** inline. To get an inline
player, edit the README on github.com and drag `testatlas-demo.mp4` into the
editor once — GitHub hosts it and inserts a `<video>` player. (Re-renders update
the committed file; re-drag if you want the newest cut embedded inline.)

## Changing the demo

Edit the relevant source and push to `main`; the workflow re-renders:

- browser beats & pacing → [`screencast.mjs`](screencast.mjs)
- terminal intro → [`intro.tape`](intro.tape)
- MCP queries → [`mcp.tape`](mcp.tape) / [`mcp-ask.mjs`](mcp-ask.mjs)

To render locally you need `dotnet`, Node, `vhs`, `ttyd` and `ffmpeg`; see the
workflow for the exact commands.
