# Contributing to TestAtlas

Thanks for your interest in improving TestAtlas! 🗺️ This is a small, focused
project and contributions of all sizes are welcome — bug reports, docs fixes,
new analyzers, and feature ideas.

## Ground rules

TestAtlas has a deliberate design philosophy. Please keep changes aligned with it:

- **Zero config** — it should work by pointing at a `.sln` with no setup.
- **No AI, no network** — indexing is static and deterministic. The core CLI
  must never call out to a network or an LLM.
- **Deterministic output** — the same input solution always produces the same
  map.

Changes that break these principles are unlikely to be merged, so please open an
issue to discuss first if you're unsure.

## Getting set up

You'll need the **.NET 8.0 SDK**.

```bash
git clone https://github.com/Karzone/TestAtlas.git
cd TestAtlas
dotnet build
dotnet test
```

To try the CLI against the bundled sample:

```bash
dotnet run --project src/TestAtlas.Cli -- index TestAtlas.sln --output codemap.db
```

## Making a change

1. **Open an issue first** for anything beyond a trivial fix, so we can agree on
   the approach before you invest time.
2. Create a branch from `main`.
3. Make your change, keeping commits focused and messages descriptive.
4. Add or update tests under `tests/` — new analyzers and bug fixes should come
   with coverage.
5. Run `dotnet build` and `dotnet test` locally; make sure both pass.
6. Update the docs in `docs/` or the README if behavior or usage changed.

## Opening a pull request

- Target the `main` branch.
- Fill out the pull request template.
- Keep PRs scoped to a single concern where possible — smaller PRs get reviewed
  faster.
- Link the issue your PR closes (e.g. `Closes #12`).

## Reporting bugs and requesting features

Use the issue templates:

- **Bug report** — include the command you ran, what you expected, and what
  happened (a minimal solution that reproduces it is gold).
- **Feature request** — describe the problem you're trying to solve, not just
  the solution you have in mind.

## Security issues

Please **do not** open a public issue for security vulnerabilities. See
[SECURITY.md](SECURITY.md) for how to report them privately.

## Code of Conduct

By participating, you agree to abide by the
[Code of Conduct](CODE_OF_CONDUCT.md).

## License

By contributing, you agree that your contributions will be licensed under the
[MIT License](LICENSE) that covers this project.
