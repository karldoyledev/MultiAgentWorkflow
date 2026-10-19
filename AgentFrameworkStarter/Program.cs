using System.ClientModel;
using OpenAI;

// Keep the credential-free tutorial available as an explicit demo; the Planner -> Worker -> Validator
// workflow is the default application flow.
if (args.Length > 0 && args[0].Equals("demo", StringComparison.OrdinalIgnoreCase))
{
	await Step5WorkflowBasics.RunAsync();
	return;
}

var apiKey = Environment.GetEnvironmentVariable("ai-cubic3-apikey-1")
	?? throw new InvalidOperationException("Set the ai-cubic3-apikey-1 environment variable.");
var model = Environment.GetEnvironmentVariable("ai-cubic3-apikey-1-model")
	?? throw new InvalidOperationException("Set ai-cubic3-apikey-1-model to a model ID supported by your provider.");
var baseUrl = Environment.GetEnvironmentVariable("ai-cubic3-baseurl")
	?? "https://model.ai.cubic3.com";

var client = new OpenAIClient(
	new ApiKeyCredential(apiKey),
	new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

await PlannerWorkerValidatorWorkflow.RunAsync(client, model, args);
