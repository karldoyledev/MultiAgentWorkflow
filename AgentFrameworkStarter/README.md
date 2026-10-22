# Planner -> Worker -> Checker -> Validator Workflow

A .NET app with a dark-themed Blazor Server UI that wraps the Agent Framework workflow inputs, plus the original console flows. It runs Planner -> Worker -> Checker -> Validator workflows using the Microsoft Agent Framework and an OpenAI-compatible Chat Completions endpoint. It defaults to `https://model.ai.cubic3.com`.

## Configure

In PowerShell, set your key and a model ID supported by your provider (used by the Live mode and console runs):

```powershell
$env:ai-cubic3-apikey-1 = "your-api-key"
$env:ai-cubic3-apikey-1-model = "provider/model-id"
# Optional; this is the default:
$env:ai-cubic3-baseurl = "https://model.ai.cubic3.com"
```

Keep API keys in environment variables or a local secret manager; do not commit them.

## Run

### Web UI (default)

Start the dark-themed Blazor UI with `dotnet run` (no args) or `dotnet run -- ui`, then open http://localhost:5080:

```powershell
dotnet run
# or: dotnet run -- ui
```

The UI has a **Demo / Live switch in the top right**:
- **Demo** runs the credential-free loop smoke test (skill loading, the build/test oracle, memory round-trip, proposal parsing) — no API key required.
- **Live** runs the real Maker -> Checker -> Validator workflow, streaming the workflow's console output into the terminal panel.

The loop's options are **selected on the page, not the command line**:
- **Target pattern** — pick a preset simplification pattern (from the `code-simplification` skill) or choose "Custom…" and type your own.
- **Target file** — pick from the `.cs` files discovered in the repo, or "Custom…" for a typed path.
- **Retry budget** — choose 1–5 repair attempts.
- **Model / Base URL** — editable inputs with suggested presets (OpenAI-compatible endpoints); **API key** — password field. All three default from the environment variables above, and the key is never persisted.

Override the URL with the `ASPNETCORE_URLS` environment variable if `5080` is taken.

### Console flows

**Planner -> Worker -> Checker -> Validator** - the original console default. The Checker is a separate agent with its own rubric: it reviews the Worker's draft and either approves it or sends actionable feedback back through the bounded repair loop. Deterministic validation runs after the Checker approves. Pass the task as the remaining command-line arguments:

```powershell
dotnet run -- "Write a short release note for session persistence."
```

If no task is supplied, the workflow uses its default session-persistence release note. Required sections default to `Summary`, `Changes`, and `Risks`; deterministic validation checks for those headings, a 40-word minimum, and a short forbidden-phrase list. Checker feedback or failed validation is sent back to the Worker once; a second failure exits with the unresolved feedback listed. The retry cap lives in `PlannerWorkerValidatorWorkflow.MaxWorkerAttempts`.

**Step 5 tutorial graph** - an optional, credential-free demo with two executors and one edge:

```powershell
dotnet run -- demo
```

**Loop-engineering workflow** (skills.addy.ie/tutorials/loop-engineering/) - a bounded Maker -> Checker -> Validator loop that proposes one tiny code simplification per run and hands you PR-style evidence. Only the five skills named on the tutorial page drive the agents: `code-simplification`, `incremental-implementation`, `test-driven-development` (Maker) and `code-review-and-quality` (Checker); the Reporter follows `git-workflow-and-versioning` to emit the evidence and update the memory state file.

```powershell
# Runs with the course defaults; options (pattern, file, budget, model, key, URL) are set in the web UI.
dotnet run -- loop
```

The loop's deterministic machinery (skill loading, the `dotnet build`/`dotnet test` oracle, the memory state file, and proposal parsing) is testable without credentials:

```powershell
dotnet run -- loop-demo
```

The loop never edits your files or opens a real PR by itself: the Maker proposes a before/after diff plus the test that proves it, the Checker grades it independently, and the deterministic Validator owns the branch (loop back to the Maker up to `LoopEngineeringWorkflow.DefaultMaxAttempts`, then exit). The Reporter writes each run's outcome to `loop-state.md` (the loop's memory) so a scheduled run resumes from where the last one stopped. Applying the diff and merging stays a human decision, per the tutorial's outer-loop guidance.

`ai-cubic3-baseurl` should be the provider's API base URL (for example, an OpenAI-compatible `/api/v1` URL), not the full `/chat/completions` route.