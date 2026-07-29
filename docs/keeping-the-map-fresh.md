# Keeping the map fresh

TestAtlas answers are deterministic — but only as fresh as the map. Re-index whenever source
changes so a query never returns a *deterministically stale* answer.

A full re-index is a single static pass — **seconds** — and its cost scales with **solution size,
not with how much changed**. So re-index on *change*, not on a timer. High churn (many tests added
daily) is not a performance problem — each pass is a fixed cost, so just re-index more often. If a
pass ever gets slow on a very large solution, scope it with `--include` / `--exclude`.

## Check whether a map is stale

```bash
python scripts/check-map-age.py [path/to/map.db]   # defaults to ./codemap.db, then ./atlas.db
```

It reads the map's `generated_utc` + `solution_path`, then scans **authored** source
(`*.cs` / `*.feature`) for anything modified since — ignoring generated files
(`*.feature.cs`, `*.g.cs`, `*.designer.cs`), `bin`/`obj`, and any nested solution.

| Exit code | Meaning |
|:--:|---|
| `0` | **fresh** — no source changes since the map was built |
| `1` | **stale** — re-run `testatlas index` |
| `2` | **no map** — nothing to check |

*(Python 3, stdlib only — runs on Windows, macOS, Linux.)*

## Warn automatically after every pull (git hook)

A version-controlled `post-merge` hook runs the check after each merge / `git pull` — it only
prints, never blocks:

```bash
git config core.hooksPath scripts/hooks     # enable once, per clone
```

The hook auto-detects `codemap.db` / `atlas.db` at the repo root, or point it explicitly with
`export TESTATLAS_DB=/path/to/your/map.db`.

## Re-index in CI (any provider)

Three steps: **install the tool → run `testatlas index` → publish the `.db` artifact** (don't
commit the binary `.db`).

### GitHub Actions — `.github/workflows/testatlas.yml`

```yaml
name: TestAtlas map
on:
  push:
    branches: [ main ]
jobs:
  map:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet tool install --global TestAtlas.Cli
      - run: testatlas index YourSolution.sln --output codemap.db
      - uses: actions/upload-artifact@v4
        with:
          name: testatlas-map
          path: codemap.db
```

### Azure DevOps — `azure-pipelines.yml`

```yaml
trigger:
  branches:
    include: [ main ]
pool:
  vmImage: ubuntu-latest
steps:
  - task: UseDotNet@2
    inputs:
      packageType: sdk
      version: '8.0.x'
  - script: dotnet tool install --global TestAtlas.Cli
    displayName: Install TestAtlas
  - script: testatlas index YourSolution.sln --output codemap.db
    displayName: Index solution
  - publish: codemap.db
    artifact: testatlas-map
```

Any other CI (GitLab CI, Jenkins, TeamCity, CircleCI) follows the same three steps.
