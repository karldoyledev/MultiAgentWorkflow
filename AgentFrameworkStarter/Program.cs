using System.ClientModel;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

// The Step 5 tutorial graph needs no credentials, so it is handled before provider configuration is read.
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

var sessionDirectory = Path.Combine(
	Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
	"AgentFrameworkStarter");
Directory.CreateDirectory(sessionDirectory);
var historyPath = Path.Combine(sessionDirectory, "chat-history.json");
var historyProvider = new FileChatHistoryProvider(historyPath);

var client = new OpenAIClient(
	new ApiKeyCredential(apiKey),
	new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

var agent = client.GetChatClient(model).AsAIAgent(new ChatClientAgentOptions
{
	Name = "ToolAgent",
	ChatOptions = new ChatOptions
	{
		Instructions = "You are a helpful assistant. Keep your answers concise. For weather questions, call GetWeather and clearly identify its result as simulated, not live weather.",
		Tools = [AIFunctionFactory.Create(DemoTools.GetWeather)]
	},
	ChatHistoryProvider = historyProvider
});

// "workflow" swaps the single-agent baseline for the Planner -> Worker -> Validator graph.
if (args.Length > 0 && args[0].Equals("workflow", StringComparison.OrdinalIgnoreCase))
{
	await PlannerWorkerValidatorWorkflow.RunAsync(client, model, [.. args.Skip(1)]);
	return;
}

AgentSession session = await agent.CreateSessionAsync();
Console.WriteLine($"Conversation history is stored in {historyPath}.");
Console.WriteLine("Type '/reset' to clear it or 'exit' to quit.");

while (true)
{
	Console.Write("> ");
	var input = Console.ReadLine();
	if (input is null || input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
	{
		break;
	}

	if (input.Trim().Equals("/reset", StringComparison.OrdinalIgnoreCase))
	{
		session = await agent.CreateSessionAsync();
		await historyProvider.ClearAsync();
		Console.WriteLine("Conversation history cleared. Started a new conversation.");
		continue;
	}

	if (string.IsNullOrWhiteSpace(input))
	{
		continue;
	}

	var response = await agent.RunAsync(input, session);
	Console.WriteLine(response);
}

internal sealed class FileChatHistoryProvider : ChatHistoryProvider
{
	private readonly string _historyPath;
	private readonly SemaphoreSlim _fileLock = new(1, 1);

	public FileChatHistoryProvider(string historyPath)
	{
		_historyPath = historyPath;
	}

	public override IReadOnlyList<string> StateKeys => [];

	protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
		InvokingContext context,
		CancellationToken cancellationToken = default)
	{
		await _fileLock.WaitAsync(cancellationToken);
		try
		{
			if (!File.Exists(_historyPath))
			{
				return [];
			}

			var json = await File.ReadAllTextAsync(_historyPath, cancellationToken);
			return JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? [];
		}
		finally
		{
			_fileLock.Release();
		}
	}

	protected override async ValueTask StoreChatHistoryAsync(
		InvokedContext context,
		CancellationToken cancellationToken = default)
	{
		await _fileLock.WaitAsync(cancellationToken);
		try
		{
			var messages = new List<ChatMessage>();
			if (File.Exists(_historyPath))
			{
				var json = await File.ReadAllTextAsync(_historyPath, cancellationToken);
				messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? [];
			}

			messages.AddRange(context.RequestMessages);
			messages.AddRange(context.ResponseMessages ?? []);

			var temporaryPath = $"{_historyPath}.tmp";
			await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(messages), cancellationToken);
			File.Move(temporaryPath, _historyPath, overwrite: true);
		}
		finally
		{
			_fileLock.Release();
		}
	}

	public async Task ClearAsync()
	{
		await _fileLock.WaitAsync();
		try
		{
			File.Delete(_historyPath);
		}
		finally
		{
			_fileLock.Release();
		}
	}
}

internal static class DemoTools
{
	[Description("Return clearly labeled simulated weather for a location. This is demonstration data, not a live forecast.")]
	public static string GetWeather(
		[Description("The city or location to get simulated weather for.")] string location)
	{
		if (string.IsNullOrWhiteSpace(location))
		{
			return "A location is required.";
		}

		return $"Simulated weather for {location.Trim()}: cloudy, with a high of 15 C.";
	}
}
