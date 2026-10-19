# Planner -> Worker -> Validator Workflow

A .NET console app that runs a Planner -> Worker -> Validator workflow using the Microsoft Agent Framework and an OpenAI-compatible Chat Completions endpoint. It defaults to `https://model.ai.cubic3.com`.

## Configure

In PowerShell, set your key and a model ID supported by your provider:

```powershell
$env:ai-cubic3-apikey-1 = "your-api-key"
$env:ai-cubic3-apikey-1-model = "provider/model-id"
# Optional; this is the default:
$env:ai-cubic3-baseurl = "https://model.ai.cubic3.com"
```

Keep API keys in environment variables or a local secret manager; do not commit them.

## Run

The Planner -> Worker -> Validator workflow is the default. Pass the task as the remaining command-line arguments:

```powershell
dotnet run -- "Write a short release note for session persistence."
```

If no task is supplied, the workflow uses its default session-persistence release note. Required sections default to `Summary`, `Changes`, and `Risks`; the Validator checks for those headings, a 40-word minimum, and a short forbidden-phrase list. A failed check hands the diagnostics back to the Worker once; a second failure exits with the unresolved checks listed. The retry cap lives in `PlannerWorkerValidatorWorkflow.MaxWorkerAttempts`.

**Step 5 tutorial graph** - an optional, credential-free demo with two executors and one edge:

```powershell
dotnet run -- demo
```

`ai-cubic3-baseurl` should be the provider's API base URL (for example, an OpenAI-compatible `/api/v1` URL), not the full `/chat/completions` route.