# Factory loop harness

Repeatable, credential-free demonstration of the software-factory loop (`FactoryLoopWorkflow.cs` in
`AgentFrameworkStarter`). Every scenario drives a real executor — or the full
`Maker -> Checker -> BuildOracle -> Reporter` graph — against an **isolated throwaway project** (a
single-file console app created under `%TEMP%` and deleted afterwards), so nothing in this repository
is ever modified by the run.

This is its **own project** (`Tests/FactoryLoopHarness`), referenced from `AgentFrameworkStarter` via
`InternalsVisibleTo`. **Rule:** every change to `AgentFrameworkStarter` must keep this green —
enforced by `.github/workflows/ci.yml` on every push/PR and locally by `verify.ps1` (see `CONTRIBUTING.md`).

## Run it

```powershell
dotnet run --project Tests/FactoryLoopHarness
# or the one-command local gate:
./verify.ps1
```

Output: one block per scenario with `[PASS]/[FAIL]` per assertion plus expected vs actual, then a
summary line and a full report written to `%TEMP%\factory-harness-report.md`.

Exit code = number of failed scenarios (0 = all green), so it can gate a hook or a CI step.

## Scenarios and expected outputs

| # | Scenario | What it drives | Expected output |
|---|----------|----------------|-----------------|
| 1 | Oracle accepts a clean project | `RunDotNetBuildOracleAsync` on the scratch project | `dotnet build` exit `0`, log contains `Build succeeded` |
| 2 | Oracle rejects a change that breaks the build, then accepts the fix | Write `class Broken {` (syntax error), build, restore, build | broken: exit non-zero and log contains `error`; fixed: exit `0` |
| 3 | ScriptedMaker removes trailing whitespace and reports the change | `ScriptedCleanerMakerExecutor` on a file with trailing spaces | a `FactoryChange` is produced, before ≠ after, no trailing whitespace remains, summary is `Removed trailing whitespace` |
| 4 | ScriptedMaker produces no change on a clean file | same executor on a clean file | `Change == null` (honest "nothing to do") |
| 5 | ScriptedChecker approves small diffs and rejects large ones | `ScriptedApproverChecker` on a 2-line change and a 60-line change | small → `Approved == true`; large → `Approved == false` with feedback containing `larger than a few lines` |
| 6 | Full loop: a fixable change reaches a passing report and memory file | complete graph, file seeded with trailing whitespace | report contains `=== Deliverable ===`, `Checker approved and dotnet build passed (exit 0) after 1 maker attempt(s).`, `=== Human step (outer loop) ===`; memory file written with `=== Validation ===` and `Removed trailing whitespace` |
| 7 | Full loop: nothing to fix exits with the honest failure report | complete graph, clean file | report contains `Did not pass within 2 attempt(s)` and `The Maker produced no change this attempt.`; the memory file records the same |
| 8 | Tool helpers | `SplitCommandLine`, `GitDiffBetween`, repo-write guard | `"a b"` parses as one token; equal snapshots → `(no textual change)`; differing → a diff; `WriteFile` outside the repo → `refusing` |

## Why the expected outputs look like this

- **Scenarios 1–2** pin the oracle: pass = exit code 0, and a genuinely broken project is caught (the
  "cheap, hard-to-fake check" the loop's autonomy rests on).
- **Scenarios 3–4** pin the Maker's contract: a real change is produced *only* when there is
  something to fix — the loop must never invent work.
- **Scenario 5** pins the Checker's scope gate: small focused diffs pass; sweeping rewrites are sent
  back with feedback (the tutorial's "reject anything larger than a few lines").
- **Scenarios 6–7** prove the whole graph: accepted changes end in a passing evidence report plus a
  memory file entry; nothing-to-fix ends in an honest failure report after exactly `MaxAttempts` (2)
  tries — no infinite loop, no fabricated success.
- **Scenario 8** pins the tool safety rails the model-backed Maker relies on.
