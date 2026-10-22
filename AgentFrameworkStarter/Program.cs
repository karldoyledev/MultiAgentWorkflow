using System.ClientModel;
using OpenAI;

// Keep the credential-free tutorial available as an explicit demo; the web UI is now the default entry point.
if (args.Length > 0 && args[0].Equals("demo", StringComparison.OrdinalIgnoreCase))
{
	await Step5WorkflowBasics.RunAsync();
	return;
}

// Credential-free smoke test for the loop's deterministic machinery (skills, oracle, memory, parsing).
if (args.Length > 0 && args[0].Equals("loop-demo", StringComparison.OrdinalIgnoreCase))
{
	await LoopEngineeringWorkflow.RunSmokeAsync();
	return;
}

// Credential-free PoC of the software-factory loop: scripted Maker on a synthetic target + the REAL
// `dotnet build` oracle + git-diff evidence + memory file. No model calls, no API key required.
if (args.Length > 0 && args[0].Equals("dev-loop-demo", StringComparison.OrdinalIgnoreCase))
{
	await FactoryLoopWorkflow.RunDemoAsync();
	return;
}

// The dark-themed web UI is the default: run `dotnet run` (no args) or `dotnet run -- ui`.
if (args.Length == 0 || IsUiAlias(args[0]))
{
	await WebUi.StartAsync(args.Length > 0 ? args[1..] : []);
	return;
}

// Console modes that need a model endpoint.
var apiKey = Environment.GetEnvironmentVariable("ai-cubic3-apikey-1")
	?? throw new InvalidOperationException("Set the ai-cubic3-apikey-1 environment variable.");
var model = Environment.GetEnvironmentVariable("ai-cubic3-apikey-1-model")
	?? throw new InvalidOperationException("Set ai-cubic3-apikey-1-model to a model ID supported by your provider.");
var baseUrl = Environment.GetEnvironmentVariable("ai-cubic3-baseurl")
	?? "https://model.ai.cubic3.com";

var client = new OpenAIClient(
	new ApiKeyCredential(apiKey),
	new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

// The loop-engineering workflow is available as an explicit console subcommand, running with the
// course defaults. Its options (pattern, file, retry budget, model, key, base URL) are owned by the
// web UI now, not the command line.
if (args.Length > 0 && args[0].Equals("loop", StringComparison.OrdinalIgnoreCase))
{
	await LoopEngineeringWorkflow.RunAsync(client, model, LoopEngineeringWorkflow.BuildDefaultRequest());
	return;
}

// The software-factory PoC (Live): a tool-using Maker (read/write/run/diff) whose edits are gated by
// a REAL `dotnet build` oracle, then handed back as evidence for the human outer loop.
if (args.Length > 0 && args[0].Equals("dev-loop", StringComparison.OrdinalIgnoreCase))
{
	await FactoryLoopWorkflow.RunLiveAsync(client, model);
	return;
}

// Default console flow: Planner -> Worker -> Checker -> Validator.
await PlannerWorkerValidatorWorkflow.RunAsync(client, model, args);

static bool IsUiAlias(string arg) =>
	arg.Equals("ui", StringComparison.OrdinalIgnoreCase)
	|| arg.Equals("web", StringComparison.OrdinalIgnoreCase)
	|| arg.Equals("serve", StringComparison.OrdinalIgnoreCase)
	|| arg.Equals("blazor", StringComparison.OrdinalIgnoreCase);

