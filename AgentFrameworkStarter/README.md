# Microsoft Agent Framework Starter

A minimal .NET console agent using the OpenAI Chat Completions API. It defaults to `https://model.ai.cubic3.com` and can be pointed at any OpenAI-compatible endpoint.

The project also contains the Agent Framework workflow from Step 5 of the "Get Started" series: a Planner -> Worker -> Validator graph with a bounded repair path.

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

The first argument selects the mode.

**Step 5 tutorial graph** - two executors, one edge, no model calls, so no credentials are needed:

```powershell
dotnet run -- demo
```

Expected output:

```
UppercaseExecutor: HELLO, WORLD!
ReverseTextExecutor: !DLROW ,OLLEH
Workflow output from ReverseTextExecutor: !DLROW ,OLLEH
```

**Workflow** - Planner agent -> Worker agent -> deterministic Validator, with a bounded repair path:

```powershell
dotnet run -- workflow "Write a short release note for session persistence."
```

The task argument is optional and defaults to the session-persistence release note. Required sections default to `Summary`, `Changes`, and `Risks`; the Validator checks for those headings, a 40-word minimum, and a short forbidden-phrase list. A failed check hands the diagnostics back to the Worker once; a second failure exits with the unresolved checks listed. The retry cap lives in `PlannerWorkerValidatorWorkflow.MaxWorkerAttempts`.

**Single-agent baseline** - no argument, the interactive chat whose history is stored in `chat-history.json`:

```powershell
dotnet run
```

`ai-cubic3-baseurl` should be the provider's API base URL (for example, an OpenAI-compatible `/api/v1` URL), not the full `/chat/completions` route.