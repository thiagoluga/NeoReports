# Consumer smoke test

Verifies the **published** NeoReports packages the way a customer consumes them — restored from
nuget.org, no `ProjectReference`, no repo build settings.

```bash
dotnet run --project tools/consumer-smoke
```

Exit code `0` means every check passed. Each check prints `ok` or `FAIL`, and failures are collected
rather than thrown one at a time, so a broken release shows all of its problems in one run.

## What it checks

**Without a license** (always):

- The three assemblies report the expected version, and `NeoReports.Licensing` carries a
  `2.0.0+<commit>` informational version, pinning the artifact to the commit the release was built
  from.
- The embedded signing key is the production one and **not** the burned placeholder — read off the
  loaded assembly, which is what a customer's process verifies signatures against.
- All three Pro packages **refuse to work without a license**, through both the static fluent API and
  the DI registration. This is the check worth having: a Pro package that works unlicensed is revenue
  walking out of the door and looks perfectly healthy from the outside.

**With `NEOREPORTS_LICENSE_KEY` set** (adds):

- The key is accepted by the published validator.
- A real sectioned-workbook report runs end to end and the resulting `.xlsx` is opened and inspected —
  "the pipeline reported Completed" is not the same claim as "this is a workbook with two worksheets".

## Why this exists instead of converting the samples

The obvious alternative was to point `samples/15-aspire-pro-demo` at the published packages. It was
rejected, and the reason is worth keeping:

The samples are the repo's **compile-time canary**. Break a public API in `Core` today and samples 14
and 15 stop building immediately. On a pinned `PackageReference` they would happily keep building
against the last release, and the break would ship. Trading that canary for a demonstration of "how a
customer installs" is a bad exchange, because the second goal has this cheaper answer — which proves
more, since it exercises the artifact on nuget.org rather than a local build of it.

It also could not have been done by halves: `samples/15` reaches the NeoReports projects through
`AllSourcesShared`, which sample 14 uses too. Converting only the Pro references would put a *package*
`NeoReports.Core` and a *project* `NeoReports.Core` in the same build.

## Isolation, and how it breaks

`Directory.Build.props` here does not import the repo's, and the adjacent empty
`Directory.Packages.props` stops the walk-up to Central Package Management. That is what makes the
versions in the `.csproj` the ones a customer would type.

**Adding a single `ProjectReference` silently defeats the whole thing** — every check would still
pass, while testing the working tree. No assertion can catch it (the SDK copies package assemblies
into `bin/`, so `Assembly.Location` looks identical either way); the guard is the comment in the
`.csproj` and this paragraph.

## After a release

Bump the version in the `.csproj` and run it. A version here older than the newest tag means that
release has not been smoke-tested.

## Getting a license key into the variable

```bash
# bash
export NEOREPORTS_LICENSE_KEY="$(dotnet run --project tools/NeoReports.LicenseTool -- \
    sign --key <vaulted-key.pem> --licensee "Smoke test" --days 1)"
```

```powershell
# PowerShell — note -Encoding utf8. A plain `>` redirect writes UTF-16, which arrives as embedded
# NUL bytes and reads as "license key is malformed". The harness strips them anyway, but other
# tools will not.
dotnet run --project tools/NeoReports.LicenseTool -- `
    sign --key <vaulted-key.pem> --licensee "Smoke test" --days 1 |
    Out-File check.key -Encoding utf8
$env:NEOREPORTS_LICENSE_KEY = (Get-Content check.key -Raw).Trim()
```
