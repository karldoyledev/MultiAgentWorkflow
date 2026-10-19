using System.ClientModel;
using System.Text;
using OpenAI;

namespace AgentFrameworkStarter.Services;

/// <summary>
/// Runs the loop-engineering workflow (Demo smoke test or Live model run) and captures the Console
/// output the workflow writes, re-raising it as an event so the UI's terminal panel can stream it.
/// A single run is in-flight at a time.
/// </summary>
public sealed class WorkflowRunner
{
	// Console.SetOut/SetError are process-global, and WorkflowRunner is scoped per Blazor circuit,
	// so only one run may redirect console output at a time across the whole process. Without this
	// gate, two circuits running at once would interleave their output and corrupt each other's buffer.
	private static readonly SemaphoreSlim ProcessGate = new(1, 1);

	private readonly object _gate = new();
	private readonly StringBuilder _buffer = new();

	/// <summary>Raised with each chunk of captured console output while a run is active.</summary>
	public event Action<string>? OutputAppended;

	/// <summary>All output captured so far for the current run.</summary>
	public string Output
	{
		get
		{
			lock (_gate)
			{
				return _buffer.ToString();
			}
		}
	}

	public void ClearOutput()
	{
		lock (_gate)
		{
			_buffer.Clear();
		}

		OutputAppended?.Invoke(string.Empty);
	}

	/// <summary>Appends an error line to the terminal (used by the UI when a run throws).</summary>
	public Task AppendErrorAsync(string message)
	{
		Append($"[error] {message}{Environment.NewLine}");
		return Task.CompletedTask;
	}

	/// <summary>Runs the credential-free smoke test (Demo mode).</summary>
	public async Task RunDemoAsync()
		=> await RunCapturedAsync(() => LoopEngineeringWorkflow.RunSmokeAsync());

	/// <summary>Runs the full model workflow (Live mode) with the inputs from the UI form.</summary>
	public async Task RunLiveAsync(RunInputs inputs)
	{
		if (string.IsNullOrWhiteSpace(inputs.Model))
		{
			throw new InvalidOperationException("Enter a model ID (or set ai-cubic3-apikey-1-model).");
		}

		if (string.IsNullOrWhiteSpace(inputs.ApiKey))
		{
			throw new InvalidOperationException("Enter an API key (or set ai-cubic3-apikey-1).");
		}

		if (string.IsNullOrWhiteSpace(inputs.BaseUrl) || !Uri.TryCreate(inputs.BaseUrl, UriKind.Absolute, out var endpoint))
		{
			throw new InvalidOperationException("Enter a valid absolute base URL (or set ai-cubic3-baseurl).");
		}

		var client = new OpenAIClient(
			new ApiKeyCredential(inputs.ApiKey),
			new OpenAIClientOptions { Endpoint = endpoint });

		var root = ProjectRoot.Find(inputs.TargetFile);
		var request = new LoopRequest(
			TargetPattern: inputs.TargetPattern,
			TargetFilePath: inputs.TargetFile,
			StateFilePath: Path.Combine(root, "loop-state.md"),
			MaxAttempts: inputs.MaxAttempts);

		await RunCapturedAsync(() => LoopEngineeringWorkflow.RunAsync(client, inputs.Model, request));
	}

	private async Task RunCapturedAsync(Func<Task> run)
	{
		// Wait for any other in-flight run (possibly on another circuit) before touching the
		// process-global Console, so captures never interleave.
		await ProcessGate.WaitAsync();
		try
		{
			lock (_gate)
			{
				_buffer.Clear();
			}

			var originalOut = Console.Out;
			var originalError = Console.Error;

			using var capture = new CaptureWriter(Append);
			Console.SetOut(capture);
			Console.SetError(capture);

			try
			{
				await run();
			}
			finally
			{
				Console.SetOut(originalOut);
				Console.SetError(originalError);
			}
		}
		finally
		{
			ProcessGate.Release();
		}
	}

	private void Append(string text)
	{
		lock (_gate)
		{
			_buffer.Append(text);
		}

		OutputAppended?.Invoke(text);
	}

	/// <summary>A TextWriter that forwards writes to the captured buffer and the output event.</summary>
	private sealed class CaptureWriter(Action<string> onWrite) : TextWriter
	{
		public override Encoding Encoding => Encoding.UTF8;

		public override void Write(char value) => onWrite(value.ToString());

		public override void Write(string? value)
		{
			if (value is not null)
			{
				onWrite(value);
			}
		}

		public override void WriteLine(string? value) => onWrite((value ?? string.Empty) + Environment.NewLine);
	}
}

/// <summary>Inputs captured from the UI form for a Live model run.</summary>
public sealed record RunInputs(
	string TargetPattern,
	string TargetFile,
	int MaxAttempts,
	string Model,
	string ApiKey,
	string BaseUrl);
