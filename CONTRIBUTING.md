# Contributing to NeoReports

Thanks for your interest in improving NeoReports! This document explains how to
propose changes.

## Before you start

NeoReports v1 is a tightly scoped MVP. The scope, the eight non-negotiable
architecture rules, and the list of out-of-scope features are documented in
[`CLAUDE.md`](CLAUDE.md), [`docs/MVP-Spec.md`](docs/MVP-Spec.md), and the
decision log [`NeoReports-Decisoes.md`](NeoReports-Decisoes.md). Please skim
them first — a change that conflicts with a recorded decision will need that
decision revisited before it can be merged.

If you want to work on something out of scope, **open an issue first** so we can
record the decision before any code is written.

## Development setup

You need the **.NET 8 and .NET 9 SDKs**. Docker is required to run the SQL
integration tests (they use Testcontainers and skip when Docker is absent).

```bash
dotnet build
dotnet test
dotnet format
```

## Pull request checklist

- [ ] One focused change per PR, small and independent.
- [ ] Builds clean: `dotnet build` (warnings are errors).
- [ ] Tests pass: `dotnet test` — and new behavior is covered by tests.
- [ ] Style applied: `dotnet format --verify-no-changes` passes.
- [ ] Public API has XML doc comments (the library ships docs).
- [ ] If you changed a recorded decision, update `NeoReports-Decisoes.md` in the
      same PR.

CI (build · test · format on .NET 8 and 9) must be green before a PR is merged.

## Coding conventions

- C# with `Nullable` enabled, file-scoped namespaces, `sealed` by default.
- Identifiers and XML docs in **English** (this is a public OSS library).
- Async for all I/O, with `CancellationToken` as the last parameter.
- Package versions are centrally managed in
  [`build/Directory.Packages.props`](build/Directory.Packages.props) — never
  inline a version in a `.csproj`.

## Commits

We use [Conventional Commits](https://www.conventionalcommits.org/) (e.g.
`feat:`, `fix:`, `docs:`, `chore:`, `test:`).

## Reporting bugs and requesting features

Use the issue templates. For security vulnerabilities, **do not open a public
issue** — follow [SECURITY.md](SECURITY.md).

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE).
