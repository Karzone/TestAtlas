# TestAtlas demo — segment 3 of 3: ask the map like an AI agent (terminal, VHS).
# mcp-ask.mjs is a tiny example MCP client: it spawns `testatlas-mcp`, speaks
# JSON-RPC 2.0 over stdio, and prints the server's real answer — exactly what a
# coding agent does under the hood.

Output docs/demo/mcp.mp4

Require testatlas-mcp

Set Shell "bash"
Set FontSize 15
Set Width 1120
Set Height 700
Set Padding 22
Set Framerate 24
Set TypingSpeed 35ms
Set Theme "Dracula"

Hide
Type "cd samples/SampleShop && clear"
Enter
Sleep 400ms
Show

Type "# Point an AI agent at the same map over MCP — no human running queries."
Enter
Sleep 900ms

Type "# \"Find a scenario about the cart\""
Enter
Sleep 500ms
Type "node ../../docs/demo/mcp-ask.mjs atlas.db find cart"
Enter
Sleep 3800ms

Type "# \"What breaks if I change the CartApiClient?\""
Enter
Sleep 700ms
Type "node ../../docs/demo/mcp-ask.mjs atlas.db impact class CartApiClient"
Enter
Sleep 5s
