# Samples

Small, real projects used to sanity-check the indexer against genuine framework code
(not the synthetic shims in `tests/fixtures`). These are **not** part of `TestAtlas.sln`
and are not built by CI — they're inputs you point the tool at.

## ReqnrollLoginDemo — a real Reqnroll project

A genuine **Reqnroll.xUnit 2.1.0** project (real package, real `.feature`, real code-gen),
proving the mapper works on authentic Reqnroll code, including Reqnroll's generated
scenario codebehind.

### Run it

```bash
# 1. Build once so Reqnroll's code-gen produces Features/Login.feature.cs
dotnet build samples/ReqnrollLoginDemo/LoginAutomation.sln

# 2. Map it
dotnet run --project src/CodeMap.Cli -- index samples/ReqnrollLoginDemo/LoginAutomation.sln --output reqdemo.db
dotnet run --project src/CodeMap.Cli -- stats reqdemo.db
```

### What the mapper produces

```
Indexed 1 project(s): 5 class(es), 20 method(s). unbound steps: 0, ambiguous bindings: 0.
diagnostics: 0 (0 error(s)).

project                           classes  methods
LoginAutomation                         5       20
```

- **`LoginStepDefinitions`** (authored) — the five real `[Given]/[When]/[Then]` cucumber-expression bindings.
- **`LoginFeature` / `FixtureData`** (generated) — Reqnroll's codebehind for `Login.feature`, including the
  scenario methods `SuccessfulSignIn()` and `LockedOutAfterFailures()`.
- Two generated `obj/**/xUnit.AssemblyHooks.cs` classes are also picked up. **Known gap:** a `codemap.json`
  option to exclude generated (`obj/`) sources is a slice-2 refinement — the map faithfully reports what the
  compilation contains today.

### Matcher resolution (from the tested `StepMatcher`)

`Login.feature`'s seven steps resolve to six `exact` bindings on `LoginStepDefinitions` and one `unbound`
step (`And an unbound narrative step with no binding`). Persisting these as `binds_to` / `unbound` edges is
slice 2.

> The same two commands work on **any** local `.sln`/`.csproj` — e.g. a solution on your own machine that
> this cloud sandbox can't reach.
