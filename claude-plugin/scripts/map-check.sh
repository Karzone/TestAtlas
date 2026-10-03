#!/bin/sh
# TestAtlas Claude Code plugin - SessionStart hook.
#
# Tells Claude, at the start of a session, whether this project's TestAtlas map exists and
# whether it is fresh. Stdout becomes context Claude can act on, so the wording is for Claude.
#
# A user-scoped plugin fires in EVERY project the person opens, so:
#   - no *.sln in the project dir           -> say nothing
#   - a *.sln but no codemap.db / atlas.db  -> one line: no map, run /testatlas:index
#   - a map                                 -> the checker's own fresh / stale verdict
#
# Always exits 0: SessionStart only adds stdout as context on a clean exit, and a missing or
# stale map must never look like a broken hook.

root="${CLAUDE_PROJECT_DIR:-.}"
cd "$root" 2>/dev/null || exit 0

has_sln=0
for s in "$root"/*.sln; do [ -f "$s" ] && has_sln=1 && break; done
[ "$has_sln" = 1 ] || exit 0

# The plugin registers the MCP server as `testatlas-mcp` on the PATH and the indexer as
# `testatlas`; neither is installed by the plugin itself (they are .NET global tools). When
# one is missing the server has already failed to start, so say so and hand Claude the
# commands to offer. This script only reports; it never installs anything.
missing=""
command -v testatlas-mcp >/dev/null 2>&1 || missing="TestAtlas.Mcp"
command -v testatlas >/dev/null 2>&1 || missing="${missing:+$missing }TestAtlas.Cli"
if [ -n "$missing" ]; then
    echo "TestAtlas: the .NET global tool(s) $missing are not on the PATH, so the testatlas MCP server could not start in this session. Offer to install them (needs the .NET 8 SDK or later; the same commands work in PowerShell, bash and zsh):"
    echo "  dotnet tool install --global TestAtlas.Cli"
    echo "  dotnet tool install --global TestAtlas.Mcp"
    echo "After installing, run /mcp and reconnect testatlas (or restart the session), then /testatlas:index to build the map."
    exit 0
fi

db=""
for cand in "$root/codemap.db" "$root/atlas.db"; do
    [ -f "$cand" ] && db="$cand" && break
done

if [ -z "$db" ]; then
    echo "TestAtlas: this .NET solution has no map (codemap.db) at the project root, so the testatlas tools cannot answer yet. Run /testatlas:index to build one."
    exit 0
fi

checker="${CLAUDE_PLUGIN_ROOT:-$(dirname "$0")/..}/scripts/check-map-age.py"
[ -f "$checker" ] || exit 0

if command -v python >/dev/null 2>&1; then PY=python
elif command -v python3 >/dev/null 2>&1; then PY=python3
else exit 0   # no Python: skip the freshness check silently
fi

out=$("$PY" "$checker" "$db" 2>/dev/null)
case $? in
    0) echo "TestAtlas map is fresh ($db). Use the testatlas tools to look up steps, scenarios and impact before writing tests." ;;
    1) echo "TestAtlas map is STALE - source changed since it was built. Run /testatlas:index before trusting the testatlas tools." ; echo "$out" | head -5 ;;
    *) ;;   # unreadable map: the server will report it; say nothing here
esac
exit 0
