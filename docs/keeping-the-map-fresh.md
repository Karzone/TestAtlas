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

The `trigger` re-indexes **on every merge to `main`**, so the shared map reflects what just landed —
re-indexing is a seconds-long static pass, so per-merge cost is negligible. The `schedules` block is
an optional nightly safety net; drop it if you don't want it. Publishing to a **Universal Package
feed** (rather than a per-run pipeline artifact) gives the team + agents one stable name to pull
*latest* from.

```yaml
trigger:
  branches:
    include: [ main ]           # re-index on every merge to main
schedules:
  - cron: "0 3 * * *"           # optional nightly safety net
    displayName: Nightly TestAtlas map
    branches: { include: [ main ] }
    always: true
pool:
  vmImage: ubuntu-latest
steps:
  - task: UseDotNet@2
    inputs:
      packageType: sdk
      version: '8.0.x'
  # --add-source stops a private org feed (e.g. IG-Packages) from 401-ing the public tool
  - script: dotnet tool install --global TestAtlas.Cli --add-source https://api.nuget.org/v3/index.json
    displayName: Install TestAtlas.Cli
  - script: testatlas index YourSolution.sln --output $(Build.ArtifactStagingDirectory)/codemap.db
    displayName: Index solution
  - task: UniversalPackages@0     # stable "latest" the whole team + agents pull by a fixed name
    displayName: Publish codemap to a Universal feed
    inputs:
      command: publish
      publishDirectory: $(Build.ArtifactStagingDirectory)
      vstsFeedPublish: '<project>/<feed>'
      vstsFeedPackagePublish: testatlas-map
      versionOption: patch
```

*(A plain `- publish: codemap.db` / `artifact: testatlas-map` also works, but pipeline artifacts are
per-run; a Universal feed gives one stable name consumers pull the latest from.)*

Any other CI (GitLab CI, Jenkins, TeamCity, CircleCI) follows the same three steps.

## Consume the shared map — you only need `TestAtlas.Mcp` locally

Because indexing runs in CI, a developer's machine **does not need `TestAtlas.Cli`** at all — only
`TestAtlas.Mcp`, to serve the map CI already built:

```bash
dotnet tool install --global TestAtlas.Mcp                 # the server only — no indexer needed
az artifacts universal download --feed <feed> --name testatlas-map --version '*' --path .
export TESTATLAS_DB="$(pwd)/codemap.db"                    # the MCP reads this (or pass the path in .mcp.json)
```

> [!NOTE]
> The shared map reflects **`main`** — the right baseline for "what already exists in the product".
> If you're authoring steps on a feature branch and want the map to include your *uncommitted* work,
> install `TestAtlas.Cli` too and `testatlas index` locally, then point `TESTATLAS_DB` at your local
> map. For reuse questions against the product baseline, the CI map alone is enough.
