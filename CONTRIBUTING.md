# Contributing

## The rule

**Any change to this repository — and especially any change to `AgentFrameworkStarter/` — must keep the
`Tests/FactoryLoopHarness` project green.** That harness drives the real factory-loop machinery
(Maker → Checker → BuildOracle → Reporter) against an isolated throwaway project and fails
(non-zero exit) if any scenario regresses.

Enforced by:

1. **CI (`.github/workflows/ci.yml`)** — runs `dotnet build` on the solution plus the harness on every
   push and pull request. A red pipeline blocks the merge.
2. **The local gate (`verify.ps1`)** — run it before committing to get the same check locally.
3. **The harness itself** — it never touches the working tree (it builds and edits only a `%TEMP%`
   copy), so it is safe to run on a dirty repo.

## Commands

```powershell
# One-command local gate: build + harness (must exit 0)
./verify.ps1

# Equivalent, step by step
dotnet build MultiAgentWorkflow.slnx
dotnet run --project Tests/FactoryLoopHarness/FactoryLoopHarnessTests.csproj

# Credential-free smoke of the main app's loop machinery
dotnet run --project AgentFrameworkStarter/AgentFrameworkStarter.csproj -- loop-demo
```

## How to change the harness

- Scenarios + expected outputs: `Tests/FactoryLoopHarness/FACTORY-HARNESS.md`.
- Harness code: `Tests/FactoryLoopHarness/FactoryLoopHarness.cs`.
- The harness needs the app's `internal` executors/records; that access is granted by the
  `<InternalsVisibleTo Include="FactoryLoopHarnessTests" />` entry in `AgentFrameworkStarter.csproj`.
  If you add new internal types the harness must reach, keep that entry in place.

## Branching

Trunk-based: keep `main` green, work in short-lived branches, merge within 1–3 days.

## Secrets

Never commit API keys. Configure them via environment variables
(`ai-cubic3-apikey-1`, `ai-cubic3-apikey-1-model`, `ai-cubic3-baseurl`).
