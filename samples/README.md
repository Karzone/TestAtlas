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

### What the mapper produces (slice 2a)

```
Indexed 1 project(s): 3 class(es), 18 method(s), 5 step definition(s).
diagnostics: 0 (0 error(s), 0 warning(s)).

class kinds:
  other       2
  step_class  1
```

- **`LoginStepDefinitions`** → classified **`step_class`**, with its five real `[Given]/[When]/[Then]`
  cucumber-expression **step definitions** extracted (keyword + expression + `expression_kind`).
- **`LoginFeature` / `FixtureData`** — Reqnroll's generated codebehind for `Login.feature` (the scenario
  methods `SuccessfulSignIn()` / `LockedOutAfterFailures()` live here).
- The generated `obj/**/xUnit.AssemblyHooks.cs` classes are **excluded** — slice 2a skips `obj/` and `bin/`.

### Matcher resolution (from the tested `StepMatcher`)

`Login.feature`'s seven steps resolve to six `exact` bindings on `LoginStepDefinitions` and one `unbound`
step (`And an unbound narrative step with no binding`). Persisting these as `binds_to` / `unbound` edges is
slice 2b.

> The same two commands work on **any** local `.sln`/`.csproj` — e.g. a solution on your own machine that
> this cloud sandbox can't reach.
