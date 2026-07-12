# CLAUDE.md — NeoReports

Working guide loaded automatically at the start of each session. Also read `DECISIONS.md` (locked decisions) and `docs/MVP-Spec.md` (what v1 delivers).

## Vision

NeoReports is an OSS .NET library (MIT) for generating reports from data sources, with a fluent API, constant-memory streaming, resilience, and upload to destinations. v1 is a lean, typed code-first MVP built by a single maintainer.

## v1 scope (do not expand without a recorded decision)

**In:** typed code-first · SQL source (keyset) · CSV and XLSX formats · Local and S3 destinations · jobs with a single worker (Hangfire single-server + InMemory) · Polly resilience + `IFailureStrategy` (Abort / SkipBatchAndLog) · endpoints to trigger registered reports (async/sync).

**Out (post-MVP, do not implement):** dynamic path (JSON/UI config), expression evaluation (JsonLogic/DynamicLinq), variants/coalescing, multi-worker and mid-job resume, Blazor UI, auth chain, SharePoint, PDF, `dotnet new` templates, YAML/TOML config, metrics dashboard.

If something out of scope seems necessary, **stop and record a decision** in `DECISIONS.md` before coding.

## Non-negotiable architecture rules

1. **Typed-only.** The pipeline is generic over `T`. The registration *is* the POCO. **Never** use `IDictionary<string,object?>` as the row type.
2. **Batch is the canonical model.** Everything downstream consumes `ReportBatch<T>`. `IStreamingSource<T>` is adapted into batches; it has no execution path of its own.
3. **Projection only at the writer edge.** Read/map/filter operate on `T` without boxing. The conversion to `object?[]` (schema order) happens immediately before writing. Writers are **non-generic** and receive `(object?[] row, ReportSchema)`.
4. **Cursor is an opaque serializable `string?`.** The source encodes/decodes its internal cursor. Never `object?`.
5. **Polly directly.** Resilience uses `Polly v8` (`ResiliencePipeline`). Do not create `IRetryPolicy`/`IExceptionClassifier`. The only owned abstraction is `IFailureStrategy` (decision after retries are exhausted) + threshold.
6. **Single / vertical worker.** A job is an atomic unit; if it crashes, it restarts from zero (idempotent). `ICheckpointStore` exists as a contract but is a no-op in v1.
7. **`Abstractions` is frozen.** Treat `NeoReports.Abstractions` as an ABI: strict SemVer, minimal surface. Every interface there is a liability — do not add anything the MVP does not use.
8. **Constant memory.** Never materialize the whole report in memory. Streaming source → batch → writer → output stream.

## Code conventions

- **.NET 8 and 9** (multi-target in Core and Abstractions). `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`.
- **Everything that goes into the repository is in English**: identifiers, code comments, XML docs, commit messages, PR descriptions, replies to PR comments, test names. It's a public OSS library. (The conversation with the maintainer in chat stays in PT-BR; only what is versioned/published is English.)
- `file-scoped namespaces`, `sealed` by default on classes not designed for inheritance, `record` for immutable DTOs, `init`-only properties.
- Async for everything that does I/O, always with `CancellationToken` as the last parameter.
- No external dependencies in `Abstractions` beyond `Microsoft.Extensions.Logging.Abstractions`.
- Central Package Management: versions in `build/Directory.Packages.props`, never inline in the `.csproj`.

## Folder structure

```
build/        Directory.Build.props · Directory.Packages.props · .editorconfig (at the root)
src/          NeoReports.Abstractions · NeoReports.Core · Sources/* · Formats/* · Destinations/* · Jobs/* · Integrations/*
tests/        *.UnitTests · *.IntegrationTests · NeoReports.TestKit
benchmarks/   NeoReports.Benchmarks
samples/      01-sql-to-csv-local · 02-sql-to-xlsx-s3 · 03-async-job-hangfire
docs/         MVP-Spec.md
PLAN.md                  (PR plan, at the root)
DECISIONS.md             (ADR)
global.json
```

## Commands

```bash
dotnet build                       # build the solution
dotnet test                        # all tests
dotnet test tests/NeoReports.Core.UnitTests
dotnet format                      # apply .editorconfig
dotnet run --project benchmarks/NeoReports.Benchmarks -c Release
```

## Testing strategy (include in every PR)

- **xUnit + NSubstitute.** Assertions with Shouldly (MIT; FluentAssertions left in v8 after going commercial-license — see `DECISIONS.md`).
- **Writers: golden-file tests.** Output compared byte-by-byte / line-by-line against a versioned reference file.
- **SQL source: Testcontainers** (ephemeral SQL Server/Postgres), not a DB mock.
- **Memory: BenchmarkDotNet with `MemoryDiagnoser`** on a 1M-row report — prove ~constant allocation (an MVP acceptance criterion).
- **Resilience:** a source that fails N times and then recovers; cover Abort and SkipBatchAndLog + thresholds.
- A PR only closes with passing tests. Never mark a task done with a red test.

## Domain glossary

- **Report** — the definition of an extraction (source + map + filter + outputs + destinations), registered in code by name.
- **Pipeline** — the execution of a report: read batches, process, write, upload.
- **Batch** — a page of typed records (`ReportBatch<T>`); the unit of retry and progress.
- **Cursor** — an opaque keyset-pagination token (`string?`).
- **Source** — a data origin (`IBatchSource<T>` / `IStreamingSource<T>`).
- **Writer** — a format serializer (CSV, XLSX); non-generic, receives `object?[]` + schema.
- **Destination** — the upload target for the final file (Local, S3).
- **Job** — a scheduled/queued execution instance, with persisted status.
- **FailureStrategy** — what to do after a batch's retries are exhausted (Abort / SkipBatchAndLog).

## Design / UI — permanent rule

**Always base any design/UI work on the Claude Design handoff — never invent design or diverge from its tokens/components.** The handoff was delivered (2026-07) as a runnable Blazor Server starter and lives in the repo: the UI is the Razor Class Library `src/UI/NeoReports.UI` (design-system CSS in `wwwroot/css/tokens.css` + `neoreports.css`), mounted by a host via `AddNeoReportsUI()` + `UseNeoReportsUI("<base path>")` (see D32 and sample `08-web-ui`), and `docs/ui-handoff.md` maps screen → route → components → endpoint → states. Those files are the visual source of truth.

Stack (see **D31**): Blazor Server + **pure design-system CSS — no MudBlazor**. Geist / Geist Mono fonts, Tabler outline icons, flat aesthetic (no shadows/gradients; 0.5px borders), en-US UI copy, sentence case, mono for technical data, status always color + icon + text. Endpoints the engine does not expose are `mock/future` in the handoff table — do not invent APIs for the UI.

## How to work

- Follow `PLAN.md` in order; one PR per item, small and independent.
- Every PR closes one acceptance criterion of the spec and comes with tests.
- Changed a decision? Update `DECISIONS.md` in the same PR.

## Model policy (maintainer-set, 2026-07)

Cost/quality policy for agent sessions on this repo. The agent cannot change its own
session's model — the maintainer picks it in the client; what the agent controls is
delegation and escalation advice.

- **Sessions start on Sonnet 5** (the maintainer selects it). It is the default for all
  remaining work — the tasks are well specified (handoff docs, PLAN items) and Sonnet-tier
  handles them at a fraction of the cost.
- **Escalate a bounded subtask, not the session.** When a delimited piece of work clearly
  needs more capability — an architectural decision, a hard debug after failed attempts, a
  deep review — delegate just that piece to a **subagent with `model: "opus"`**, giving it a
  self-contained prompt (subagents start cold, with none of the session context). Integrate
  the result and continue on the session model.
- **If the whole session is thrashing** (2+ failed attempts at the same root cause, or the
  task turns out to be architecturally much harder than planned), stop and tell the
  maintainer explicitly: "recommend switching this session to Opus 4.8". Do not keep
  burning attempts.
- Escalation is the exception, not the rhythm — if most subtasks are being delegated up,
  the session should just run on Opus instead.
- **Never escalate to Fable** (any surface) without an explicit maintainer request.

## Agent standing permissions (granted by the maintainer)

The agent has **standing** permission (allowed to do everything in the cycle below, without asking for confirmation each time):

### Summary — what I do on my own vs. what needs you

| I do autonomously | I need your confirmation | I never do (even if asked) |
|---|---|---|
| Read/search code, explore the repo | **Merge into `master`** | Enter credentials/passwords/tokens/card |
| Create branch, edit files | **Publish package** (NuGet) / release tag | Touch access control / third-party permissions |
| `build` / `test` / `format` / Sonar scan / run sample | Delete remote branch, force-push, rewrite history | Permanently delete data |
| `commit` + `push` on the task branch | Destructive/irreversible actions (drop schema, `reset --hard` on remote, change repo visibility) | Financial transactions |
| Open PR (`gh pr create`) | Add a **new dependency** (goes in CPM) | Solve CAPTCHA, change security settings |
| Watch CI, read/reply to PR comments | Anything "outward-facing" beyond the PR | — |
| Update docs (PLAN/ADR/CHANGELOG) via PR | — | — |

> The **merge is always yours** — I never merge, unless you explicitly say in chat that I may proceed autonomously and merge until everything currently planned is done. That authorization covers every PR in the stated scope of work — I won't ask again for each individual PR within that scope, but it does not carry over to unrelated future work or new sessions without being granted again. The Claude Code app may still show you a harness prompt to authorize some commands (push, etc.); that's the permissions UI, separate from this workflow agreement.

Detail of the autonomous items:

- **Unrestricted reading.** Any read/inspect command anywhere on the system: read/list/search files, read-only `git` (`status`, `log`, `diff`, `show`, `branch`, `ls-files`, …), inspect build/tests, etc.
- **Inspect files, run any tests, and analyze the results.**
- **Run and follow the checks/CI.** Trigger/wait for CI, read the results (`gh run`, `gh pr checks`) and diagnose failures.
- **Read and reply to PR comments on GitHub** (`gh pr view`/`comment`, review comments).
- **Create a branch and write the necessary code.**
- **Commit, push, and open a PR** (`git commit`, `git push`, `gh pr create`).
- **Wait for CI, fix whatever is needed, commit and push again** — iterate until the PR is green.

### Each-task cycle (run autonomously, without asking for confirmation)

For each `PLAN.md` item (or equivalent task), follow this end-to-end cycle:

1. **Create the branch** from up-to-date `master` (`git branch --show-current` to confirm you are not on master before coding/committing).
2. **Write all the necessary code** (and the tests).
3. **Clean build / rebuild when needed** — when in doubt about cache, delete `obj/bin` or use `--no-incremental`.
4. **Create and run all the tests**; read the actual `Passed!/Failed!` and `Build succeeded/FAILED` line.
5. **Before any commit — every commit, not just the first one on a branch — run `/code-review` and `/security-review` on the change and address what they find.** This runs in addition to (before) the build/test-green gate above, not instead of it.
6. **If everything is green** (0 failures, build ok, review gates addressed): **commit, push, and open the PR**.
7. **After opening the PR, follow the check-runs until they finish** (`check runs until done`) — confirming the PR's `headRefOid` == the latest commit.
8. **Watch the PR comments.** There are automated reviewers (e.g. analysis bots) that leave important comments, including inline ones. For each **new** comment, judge whether the suggestion makes sense:
   - **If it makes sense:** make the change, then **reply to the comment** explaining what was done and **mark it resolved**.
   - **If it does not:** **reply to the comment** explaining why you won't follow it, and **mark it resolved** anyway.
   - Resolving the PR conversation thread does **not** dismiss the underlying CodeQL alert at the repository level — also dismiss it explicitly (`PATCH /repos/{owner}/{repo}/code-scanning/alerts/{number}` with a `dismissed_reason` and `dismissed_comment`), or it lingers open forever even though the conversation looks closed.
9. **If you need to redo a step** (red CI, conflict, comment to address, etc.): fix, re-run `/code-review`/`/security-review` on the new diff, commit and push again, and repeat 4–8 until the PR is green and has no pending comments or open alerts. All of this is allowed without new confirmation.

Only **stop and ask for confirmation** at the end (merge) and for the exceptions below.

Still require explicit confirmation: **PR merge** (unless the maintainer has explicitly granted autonomous-merge authorization for the current scope of work — see the note above the summary table), deleting someone else's remote branch, force-push, and destructive/irreversible actions (`reset --hard` on remote, changing repo visibility, dropping a schema, etc.).

## Operational lessons (mistakes already made — do not repeat)

1. **Confirm the branch AND that its PR is still open BEFORE committing.** Always `git branch --show-current` before `git add`/`commit` — it must not be `master`/`main`. Also confirm the branch's PR is still **OPEN**, not already merged/closed (`gh pr view <branch> --json state`): a green PR can be merged by the maintainer at any time, and committing onto an already-merged branch orphans the commit (it never reaches master). If the PR is merged, create a NEW branch from updated master. I have committed straight to `master` by mistake (it only didn't break because I'm admin), and I once committed the real English translation onto a branch whose PR (#45) had already merged, so the change landed nowhere. All work goes on its own branch with an open PR.
2. **NEVER commit/push with a red build or test.** Before `git commit`, read the actual `Build succeeded`/`Passed!`/`Failed!` line for the changed scope. If there is `Failed: N>0` or `Build FAILED`, do not commit. I have claimed "green" without checking and merged a red PR — unacceptable.
3. **Local incremental build LIES.** Cached `obj/bin` has masked a real compile error (CS0246) that only showed up in CI. When in doubt, `--no-incremental` or delete `obj/bin` and rebuild from scratch before trusting green.
4. **An Edit that fails with "file modified since read" was NOT applied.** Re-read and redo; never assume it landed. Docs (ADR/plan) have already missed commits because of this.
5. **One command at a time when diagnosing.** Large parallel batches cascade-cancel on the first error and corrupt the view of state.
6. **CI caches status.** `gh pr checks` / `commits/<sha>/check-runs` may show an old run. Confirm the PR's `headRefOid` == your latest commit before trusting the result.
7. **Results can arrive out of order.** When you notice an inconsistency, STOP and run a sequential `git branch/status/log` check before acting.
8. **One PR at a time.** Don't open several simultaneously unless the maintainer asks; close (merge) the current one before the next.
9. **New dependencies (including test ones) go in CPM** (`build/Directory.Packages.props`). `NU1010` = missing `PackageVersion`.
10. **`dotnet format --verify-no-changes` gives a local false positive** due to autocrlf (CRLF in the working tree vs LF in the repo); CI checks out LF and passes. Don't chase `ENDOFLINE` locally; trust the CI format step.
11. **Resolving a PR review thread does NOT dismiss the underlying CodeQL alert.** These are two separate GitHub objects. Justifying a CodeQL finding via `resolveReviewThread` (GraphQL) closes the conversation on the PR but leaves the alert itself `open` at the repository level (`GET /repos/{owner}/{repo}/code-scanning/alerts`) forever, even after the PR merges. Always also dismiss the alert via `PATCH .../code-scanning/alerts/{number}` with a `dismissed_reason` (`false positive` / `won't fix` / `used in tests`). Found on 2026-07-11 after several merged PRs (G1/G2) had left 10 "justified but never actually closed" alerts sitting open.
