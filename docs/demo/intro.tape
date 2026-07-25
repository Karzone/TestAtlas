# TestAtlas demo — segment 1 of 3: generate the map (terminal, VHS).
# Rendered in CI by .github/workflows/demo.yml, then concatenated with the
# browser screencast and the MCP segment into docs/demo/testatlas-demo.mp4.

Output docs/demo/intro.mp4

Require testatlas

Set Shell "bash"
Set FontSize 17
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

Type "# 1  Map the whole .NET test solution into one SQLite file — no build, no network"
Enter
Sleep 500ms
Type "testatlas index SampleShop.sln --output atlas.db"
Enter
Sleep 7s

Type "# 2  Render the browser report and the dependency map"
Enter
Sleep 500ms
Type "testatlas report atlas.db --html report.html && testatlas map atlas.db --html map.html"
Enter
Sleep 2500ms

Type "# … now let's open them in the browser ↓"
Enter
Sleep 1800ms
