#!/usr/bin/env python3
"""
Check whether a TestAtlas map is stale relative to its source solution.

Usage:
    python check-map-age.py [path-to-map.db]

If no path is given, looks for ./codemap.db then ./atlas.db.

Reads meta.generated_utc + meta.solution_path from the map, then scans the solution's
source (*.cs / *.feature) for anything modified AFTER the map was generated.

Exit codes:
    0  fresh   - no source changes since the map was built
    1  stale   - source has changed; re-run `testatlas index`
    2  no map  - map file not found or unreadable

ASCII-only output and Python-stdlib-only, so it runs unchanged on Windows, macOS and Linux.
"""
import sys, os, sqlite3, datetime, glob

def find_db(argv):
    if len(argv) > 1:
        return argv[1]
    for cand in ("codemap.db", "atlas.db"):
        if os.path.isfile(cand):
            return cand
    return None

def main():
    db = find_db(sys.argv)
    if not db or not os.path.isfile(db):
        print("no map found (looked for codemap.db / atlas.db) - run: testatlas index")
        return 2
    try:
        c = sqlite3.connect(db)
        meta = dict(c.execute("select key, value from meta"))
        gen = datetime.datetime.fromisoformat(meta["generated_utc"].replace("Z", "+00:00"))
        sln = meta["solution_path"]
    except Exception as e:
        print("could not read map metadata from %s: %s" % (db, e))
        return 2

    gen_epoch = gen.timestamp()
    sln_dir = os.path.dirname(sln)
    now = datetime.datetime.now(datetime.timezone.utc)
    age = now - gen
    age_str = "%dd %dh %dm" % (age.days, age.seconds // 3600, (age.seconds % 3600) // 60)

    if not os.path.isdir(sln_dir):
        print("Map: %s  (generated %s, %s ago)" % (db, meta["generated_utc"], age_str))
        print("Solution path in map does not exist here: %s" % sln)
        print("(cannot check staleness - the map was built elsewhere)")
        return 0

    # Nested solutions (a separate *.sln in a subfolder) are their own codebases, not part of
    # this solution - skip their subtrees so an adjacent/nested project can't report false staleness.
    nested_roots = []
    for s in glob.glob(os.path.join(sln_dir, "**", "*.sln"), recursive=True):
        d = os.path.abspath(os.path.dirname(s))
        if d != os.path.abspath(sln_dir):
            nested_roots.append(d + os.sep)

    newer = []
    skip_dirs = (os.sep + "bin" + os.sep, os.sep + "obj" + os.sep)
    # Generated code regenerates on every build - it is not authored source, so ignore it
    # or the map would look perpetually stale (e.g. Reqnroll/SpecFlow *.feature.cs codebehind).
    gen_suffixes = (".feature.cs", ".g.cs", ".designer.cs", ".generated.cs", ".assemblyattributes.cs")
    for pat in ("**/*.cs", "**/*.feature"):
        for f in glob.glob(os.path.join(sln_dir, pat), recursive=True):
            if any(s in f for s in skip_dirs):
                continue
            if f.lower().endswith(gen_suffixes):
                continue
            af = os.path.abspath(f)
            if any(af.startswith(nr) for nr in nested_roots):
                continue
            try:
                if os.path.getmtime(f) > gen_epoch:
                    newer.append(f)
            except OSError:
                pass

    print("Map       : %s" % db)
    print("Generated : %s  (%s ago)" % (meta["generated_utc"], age_str))
    print("Solution  : %s" % sln)
    print("")
    if newer:
        print("STALE - %d source file(s) changed since the map was built:" % len(newer))
        for f in sorted(newer, key=os.path.getmtime, reverse=True)[:10]:
            ts = datetime.datetime.fromtimestamp(os.path.getmtime(f), datetime.timezone.utc).isoformat(timespec="seconds")
            print("  %s  %s" % (ts, os.path.relpath(f, sln_dir)))
        if len(newer) > 10:
            print("  ... and %d more" % (len(newer) - 10))
        print("")
        print("Re-run:  testatlas index")
        return 1
    print("FRESH - no source changes since the map was generated.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
