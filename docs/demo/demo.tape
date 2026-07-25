# TestAtlas — terminal demo
#
# This is a VHS script (https://github.com/charmbracelet/vhs). You don't run it
# by hand: the workflow at .github/workflows/demo.yml installs TestAtlas, plays
# this tape against the bundled SampleShop sample, and commits the rendered GIF
# to docs/demo/testatlas-demo.gif. Edit this file to change the demo; pushing it
# re-renders the GIF.
#
# To render locally instead:  vhs docs/demo/demo.tape   (needs vhs + ffmpeg + ttyd)

Output docs/demo/testatlas-demo.gif

Require testatlas

Set Shell "bash"
Set FontSize 18
Set Width 1100
Set Height 700
Set Padding 20
Set Framerate 24
Set TypingSpeed 40ms
Set Theme "Dracula"

# --- move into the sample solution (hidden setup) ---
Hide
Type "cd samples/SampleShop && clear"
Enter
Sleep 500ms
Show

Type "# 1  Map the whole .NET solution into one SQLite file — no build, no network"
Enter
Sleep 600ms
Type "testatlas index SampleShop.sln --output atlas.db"
Enter
Sleep 10s

Type "# 2  What's in the map?"
Enter
Sleep 500ms
Type "testatlas stats atlas.db"
Enter
Sleep 4s

Type "# 3  Before writing a new step — does one already exist?"
Enter
Sleep 500ms
Type `testatlas search atlas.db "product to the cart" --steps`
Enter
Sleep 3500ms

Type "# 4  About to change a shared API client — what breaks?"
Enter
Sleep 500ms
Type "testatlas impact atlas.db --class CartApiClient"
Enter
Sleep 4s

Type "# 5  Share a human-readable HTML snapshot"
Enter
Sleep 500ms
Type "testatlas report atlas.db --html atlas.html"
Enter
Sleep 3s

# --- tidy up the generated files (hidden) ---
Hide
Type "rm -f atlas.db atlas.html"
Enter
Show

Type "# One file. Queryable. Deterministic.  🗺️"
Enter
Sleep 3s
