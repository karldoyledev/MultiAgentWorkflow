using System.Text;
using System.Text.Json;
using AgentFrameworkStarter.Services;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using OpenAI;
using OpenAI.Chat;

/// <summary>
/// The software-factory loop: the bounded Maker -> Checker -> Validator -> Reporter graph from the
/// loop-engineering tutorial. The Maker actually ACTS (via tools), the separate Checker reviews the
/// diff with the code-review-and-quality skill, and the Validator runs a REAL oracle (`dotnet build`)
/// as the hard-to-fake gate.
///
/// This is the skills.addy.ie /tutorials/loop-engineering pattern made concrete on the Microsoft Agent
/// Framework: the harness primitives (file editing, command running, git) that Claude Code / Codex
/// supply natively are exposed explicitly as tools, and the skills supply the Maker's and Checker's
/// instructions from `.agents/skills`.
///
/// The loop never commits or opens a PR; it hands back evidence (diff + build result) for the human
/// outer loop, exactly as the tutorial's steps 6-7 describe.
/// </summary>
internal static class FactoryLoopWorkflow
{
	/// <summary>One attempt plus one repair pass; the cap stops rejected work from looping forever.</summary>
	public const int DefaultMaxAttempts = 2;

	/// <summary>The narrow, conservative default the model-backed Maker is told to start from.</summary>
	public const string DefaultTask =
		"Find and fix exactly ONE small, verifiable issue in this C# repository - for example truly dead code, "
		+ "an obvious defect, or a naming/whitespace problem. Keep the change minimal and behavior-preserving, "
		+ "and prove it by running 'dotnet build --no-restore' successfully.";

	/// <summary>
	/// The Maker's standing instructions: the tutorial's maker skills plus the harness-connector rules
	/// (act through the tools, verify with the build, report concisely). The skills are what the /loops
	/// guide says belongs in the loop: project knowledge the agent would otherwise guess.
	/// </summary>
	internal static string BuildMakerInstructions()
	{
		var b = new StringBuilder();
		b.AppendLine("You are the MAKER inside a bounded software-factory loop for this C# repository. Each run fixes exactly");
		b.AppendLine("ONE small, verifiable issue. Keep the change under ~10 lines, behavior-preserving, and never touch obj/ or bin/.");
		b.AppendLine();
		b.AppendLine("Act only from the skills below and ignore any other skill referenced inside them:");
		b.AppendLine();
		b.AppendLine("=== Skill: incremental-implementation ===");
		b.AppendLine(SkillGuide.Load("incremental-implementation", 4500));
		b.AppendLine();
		b.AppendLine("=== Skill: test-driven-development ===");
		b.AppendLine(SkillGuide.Load("test-driven-development", 5000));
		b.AppendLine();
		b.AppendLine("Use the provided tools:");
		b.AppendLine("- read_file(path): read source files (paths relative to the repository root).");
		b.AppendLine("- write_file(path, content): apply your change.");
		b.AppendLine("- run_command(command, args): runs 'dotnet' or 'git' in the repository root. Use 'dotnet build --no-restore'");
		b.AppendLine("  to verify your change still compiles; fix anything you broke before finishing.");
		b.AppendLine("- git_diff(path): show your working-tree diff.");
		b.AppendLine();
		b.AppendLine("Procedure:");
		b.AppendLine("1) read_file / git_diff to pick one small issue and inspect the file.");
		b.AppendLine("2) write_file to apply the minimal fix.");
		b.AppendLine("3) run_command 'dotnet build --no-restore'; only finish when it exits 0.");
		b.AppendLine("4) git_diff the file to confirm the change is small and focused.");
		b.AppendLine("5) Finish with exactly two final lines, nothing after:");
		b.AppendLine("FILE: <relative path of the changed file>");
		b.AppendLine("SUMMARY: <one accurate sentence>");
		return b.ToString();
	}

	/// <summary>The Checker's standing instructions: the tutorial's checker skill plus the scope rubric.</summary>
	internal static string BuildCheckerInstructions()
	{
		var b = new StringBuilder();
		b.AppendLine("You are the CHECKER in a bounded software-factory loop. You are a separate model from the maker:");
		b.AppendLine("grade the maker's work, never your own. Judge only the task scope and this rubric:");
		b.AppendLine("- It addresses the requested task and stays within its scope.");
		b.AppendLine("- It is small (a few lines) and behavior-preserving; reject sweeping rewrites.");
		b.AppendLine("- It is complete, clear, and specific; no placeholders or process commentary.");
		b.AppendLine("- It is verifiable: a check (such as a build or test) could prove it.");
		b.AppendLine("Act only from the skill below:");
		b.AppendLine();
		b.AppendLine("=== Skill: code-review-and-quality ===");
		b.AppendLine(SkillGuide.Load("code-review-and-quality", 9000));
		b.AppendLine();
		b.AppendLine("Return only a JSON object with this exact shape:");
		b.AppendLine("{\"approved\":true,\"feedback\":[]} when all checks pass, or");
		b.AppendLine("{\"approved\":false,\"feedback\":[\"specific, actionable problem\"]} when rejecting.");
		b.AppendLine("Feedback must identify every problem the Maker should fix.");
		return b.ToString();
	}

	/// <summary>Builds the separate Checker agent with the code-review-and-quality skill loaded.</summary>
	internal static AIAgent CreateCheckerAgent(OpenAIClient client, string model) =>
		client.GetChatClient(model).AsAIAgent(new ChatClientAgentOptions
		{
			Name = "FactoryChecker",
			ChatOptions = new Microsoft.Extensions.AI.ChatOptions { Instructions = BuildCheckerInstructions() }
		});

	private const string DemoTargetFileName = "FactoryDemo.Target.cs";

	/// <summary>A tiny compilable file with deliberately cosmetic defects (trailing whitespace) for the credential-free demo.</summary>
	private const string DemoScratchContent =
		"""
		// Factory loop demo target: a tiny compilable file with cosmetic defects (trailing whitespace).   
		namespace AgentFrameworkStarter;   

		internal sealed class FactoryDemoTarget   
		{
		    public static string Greet() => "hello";   

		    public static int Add(int a, int b) => a + b;   

		    public static string Which() => "target";   
		}
		""";

	/// <summary>Live, model-backed run: tool-using Maker + the real build oracle.</summary>
	public static async Task RunLiveAsync(OpenAIClient client, string model, string? stateFilePath = null)
	{
		var projectRoot = FactoryDevTools.FindProjectRoot() ?? Environment.CurrentDirectory;
		var request = new FactoryRequest(
			Task: DefaultTask,
			TargetFile: null,
			StateFilePath: stateFilePath ?? Path.Combine(projectRoot, "loop-state.md"),
			MaxAttempts: DefaultMaxAttempts);

		await RunGraphAsync(
			new FactorySelectorExecutor(),
			new ToolCallingMakerExecutor(client.GetChatClient(model), projectRoot),
			new FactoryCheckerExecutor(CreateCheckerAgent(client, model)),
			new FactoryBuildOracleValidator(projectRoot),
			new FactoryReporterExecutor(request.StateFilePath),
			request);
	}

	/// <summary>
	/// Credential-free demo: a scripted Maker cleans one synthetic target file, the SAME real
	/// `dotnet build` oracle validates it, and the Reporter writes evidence to a temp memory file.
	/// </summary>
	public static async Task RunDemoAsync()
	{
		var projectRoot = FactoryDevTools.FindProjectRoot() ?? Environment.CurrentDirectory;
		var target = Path.Combine(projectRoot, DemoTargetFileName);
		File.WriteAllText(target, DemoScratchContent);

		var stateFile = Path.Combine(Path.GetTempPath(), "factory-loop-state-" + Guid.NewGuid().ToString("N") + ".md");
		try
		{
			var request = new FactoryRequest(
				Task: $"Remove all trailing whitespace from {DemoTargetFileName} and confirm the project still builds.",
				TargetFile: target,
				StateFilePath: stateFile,
				MaxAttempts: DefaultMaxAttempts);

			await RunGraphAsync(
				new FactorySelectorExecutor(),
				new ScriptedCleanerMakerExecutor(projectRoot),
				new ScriptedApproverChecker(),
				new FactoryBuildOracleValidator(projectRoot),
				new FactoryReporterExecutor(stateFile),
				request);
		}
		finally
		{
			try
			{
				File.Delete(target);
			}
			catch
		{
			// best effort; a stray scratch file would still compile
		}

		Console.WriteLine();
		Console.WriteLine($"(demo cleanup: removed {DemoTargetFileName}; memory state kept at {stateFile})");
	}
}

internal static async Task<string> RunGraphAsync(
		FactorySelectorExecutor selector,
		Executor<FactoryStep, FactoryOutput> maker,
		Executor<FactoryOutput, FactoryReview> checker,
		FactoryBuildOracleValidator validator,
		FactoryReporterExecutor reporter,
		FactoryRequest request)
	{
		WorkflowBuilder builder = new(selector);
		builder.AddEdge(selector, maker);
		builder.AddEdge(maker, checker);
		builder.AddEdge(checker, validator);

		// The checker+oracle gate owns the branch: a rejected or unbuildable attempt re-enters the Maker up to the cap.
		builder.AddEdge<FactoryStep>(validator, maker, step => step?.NextAttempt is not null);
		builder.AddEdge<FactoryStep>(validator, reporter, step => step?.NextAttempt is null);
		builder.WithOutputFrom(reporter);

		var workflow = builder.Build();

		Console.WriteLine($"Task              : {request.Task}");
		Console.WriteLine($"State file        : {request.StateFilePath}");
		Console.WriteLine($"Retry budget      : {request.MaxAttempts} maker attempt(s)");
		Console.WriteLine();

		await using Run run = await InProcessExecution.RunAsync(workflow, request);

		string? finalReport = null;
		foreach (WorkflowEvent workflowEvent in run.NewEvents)
		{
			switch (workflowEvent)
			{
				case ExecutorInvokedEvent invoked:
					Console.WriteLine($"[run] {invoked.ExecutorId} invoked");
					break;

				case ExecutorCompletedEvent completed:
					Console.WriteLine($"[run] {completed.ExecutorId} completed: {WorkflowFormat.Preview(completed.Data?.ToString() ?? string.Empty)}");
					break;

				case WorkflowOutputEvent output:
					Console.WriteLine();
					Console.WriteLine(output.Data);
					finalReport = output.Data?.ToString() ?? string.Empty;
					break;
			}
		}

		return finalReport ?? string.Empty;
	}

	internal static string BuildMakerPrompt(FactoryBrief brief)
	{
		var b = new StringBuilder();
		b.AppendLine("Task:");
		b.AppendLine(brief.Task);

		if (!string.IsNullOrWhiteSpace(brief.TargetFile))
		{
			b.AppendLine();
			b.AppendLine("Target file: " + brief.TargetFile);
		}

		if (brief.Feedback.Count > 0)
		{
			b.AppendLine();
			b.AppendLine("A previous attempt failed these check(s). Fix every one of them and keep whatever already passed:");
			foreach (var failure in brief.Feedback)
			{
				b.AppendLine($"- {failure}");
			}
		}

		if (!string.IsNullOrWhiteSpace(brief.Memory))
		{
			b.AppendLine();
			b.AppendLine("Memory (prior state file, for context - do not repeat finished work):");
			b.AppendLine(brief.Memory);
		}

		b.AppendLine();
		b.AppendLine("End with exactly two final lines, nothing after them:");
		b.AppendLine("FILE: <relative path of the changed file>");
		b.AppendLine("SUMMARY: <one accurate sentence>");
		return b.ToString();
	}

	/// <summary>Caps a tool result or log so a wayward model output cannot flood the conversation.</summary>
	internal static string Truncate(string text, int maxLength = 2000)
	{
		if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
		{
			return text;
		}

		return text[..maxLength] + $"\n... (truncated {text.Length - maxLength} chars)";
	}

	/// <summary>Extracts the lines that matter from a build log (errors first, then a fallback tail).</summary>
	internal static string BuildErrors(string output)
	{
		var lines = output.Replace("\r\n", "\n").Split('\n');
		var errors = lines.Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();
		var verdict = errors.Count > 0
			? string.Join(Environment.NewLine, errors.Take(20))
			: string.Join(Environment.NewLine, lines.TakeLast(20));
		return verdict.Trim();
	}

	internal static string RemoveTrailingWhitespace(string text)
	{
		var normalized = text.Replace("\r\n", "\n");
		var lines = normalized.Split('\n');
		var cleaned = string.Join("\n", lines.Select(line => line.TrimEnd(' ', '\t')));
		cleaned = cleaned.TrimEnd('\n') + "\n";
		return cleaned;
	}
}

/// <summary>The loop's input: the narrow task, an optional fixed target file, memory location, and retry cap.</summary>
internal sealed record FactoryRequest(string Task, string? TargetFile, string StateFilePath, int MaxAttempts);

/// <summary>Everything the Maker needs for one attempt, including why the previous one fell short.</summary>
internal sealed record FactoryBrief(
	string Task,
	string? TargetFile,
	int Attempt,
	IReadOnlyList<string> Feedback,
	string Memory,
	int MaxAttempts)
{
	public FactoryBrief ForRetry(IReadOnlyList<string> feedback) =>
		this with { Attempt = Attempt + 1, Feedback = feedback };
}

/// <summary>The materialized change the Maker made, with captured before/after snapshots when available.</summary>
internal sealed record FactoryChange(string FilePath, string Summary, string? BeforeContent = null, string? AfterContent = null)
{
	/// <summary>Human-readable evidence: before/after diff when captured, otherwise the working-tree diff.</summary>
	public string Evidence => BeforeContent is not null
		? FactoryDevTools.GitDiffBetween(BeforeContent, AfterContent ?? string.Empty)
		: FactoryDevTools.GitDiffWorkingTree(FilePath);
}

/// <summary>The Maker's result: either a real change or "nothing to change" for this attempt.</summary>
internal sealed record FactoryOutput(FactoryChange? Change, FactoryBrief Brief);

/// <summary>The Checker's independent verdict on the Maker's diff, and the feedback if it rejects.</summary>
internal sealed record FactoryReview(FactoryOutput Output, bool Approved, IReadOnlyList<string> Feedback);

/// <summary>
/// The one message that travels around the loop in both directions: the next attempt, or the final
/// report that ends the run (same shape as LoopStep, file-centric).
/// </summary>
internal sealed record FactoryStep(FactoryBrief? NextAttempt, string? FinalReport)
{
	public static FactoryStep ForAttempt(FactoryBrief brief) => new(brief, null);

	public static FactoryStep Done(string report) => new(null, report);
}

/// <summary>The loop's entry point: builds the first brief from the request and reads the memory state file.</summary>
internal sealed class FactorySelectorExecutor() : Executor<FactoryRequest, FactoryStep>("Selector")
{
	public override ValueTask<FactoryStep> HandleAsync(
		FactoryRequest request,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var memory = LoopMemory.Read(request.StateFilePath);
		var brief = new FactoryBrief(
			Task: request.Task,
			TargetFile: request.TargetFile,
			Attempt: 1,
			Feedback: [],
			Memory: memory,
			MaxAttempts: request.MaxAttempts);
		return ValueTask.FromResult(FactoryStep.ForAttempt(brief));
	}
}

/// <summary>
/// Deterministic, credential-free Maker: removes trailing whitespace from the target file (the same
/// repair path the model-backed Maker would take, scripted). Produces no change when there is nothing
/// to fix, which the oracle then treats as a failed attempt.
/// </summary>
internal sealed class ScriptedCleanerMakerExecutor(string? projectRoot = null) : Executor<FactoryStep, FactoryOutput>("CleanerMaker")
{
	public override ValueTask<FactoryOutput> HandleAsync(
		FactoryStep message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var brief = message.NextAttempt
			?? throw new InvalidOperationException("The maker only runs when the graph supplies a next attempt.");
		var root = projectRoot ?? FactoryDevTools.FindProjectRoot() ?? Environment.CurrentDirectory;
		var target = FactoryDevTools.ResolvePath(brief.TargetFile ?? string.Empty, root);
		var before = FactoryDevTools.ReadFile(target);
		var cleaned = FactoryLoopWorkflow.RemoveTrailingWhitespace(before);

		if (cleaned == before)
		{
			Console.WriteLine($"[Maker] attempt {brief.Attempt}: no trailing whitespace found in {target}; nothing to change.");
			return ValueTask.FromResult(new FactoryOutput(null, brief));
		}

		FactoryDevTools.WriteFile(target, cleaned);
		Console.WriteLine($"[Maker] attempt {brief.Attempt}: removed trailing whitespace from {target}.");
		return ValueTask.FromResult(new FactoryOutput(
			new FactoryChange(target, "Removed trailing whitespace", before, cleaned),
			brief));
	}
}



/// <summary>
/// The tool-using Maker (Live mode): an explicitly tool-calling model backed by the OpenAI SDK. It is
/// given read_file / write_file / run_command / git_diff and loops over function calls itself, applying
/// real edits, verifying with 'dotnet build', then reporting FILE:/SUMMARY: lines the loop can parse.
/// </summary>

/// <summary>
/// The separate Checker (Live mode). A second, independent model reviews the Maker's diff against the
/// task and the code-review-and-quality rubric, returning a JSON approve/reject decision. It can only
/// see evidence, never the Maker's own reasoning, so its verdict is the tutorial's trust anchor.
/// </summary>
internal sealed class FactoryCheckerExecutor(AIAgent checker) : Executor<FactoryOutput, FactoryReview>("Checker")
{
	public override async ValueTask<FactoryReview> HandleAsync(
		FactoryOutput output,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var brief = output.Brief;
		var change = output.Change;
		if (change is null)
		{
			return new FactoryReview(output, false, ["The Maker produced no change this attempt."]);
		}

		var prompt =
			$$"""
			Task:
			{{brief.Task}}

			Changed file: {{change.FilePath}}
			Maker's summary: {{change.Summary}}

			Diff:
			<diff>
			{{FactoryLoopWorkflow.Truncate(change.Evidence, 8000)}}
			</diff>

			Judge the work against your rubric and only the task scope.
			Return only a JSON object with this exact shape:
			{"approved":true,"feedback":[]} when all checks pass, or
			{"approved":false,"feedback":["specific, actionable problem"]} when rejecting.
			Feedback must identify every problem the Maker should fix.
			""";

		var response = (await checker.RunAsync(prompt, cancellationToken: cancellationToken)).Text
			?? throw new InvalidOperationException("The Checker returned no review.");
		var decision = PlannerWorkerValidatorWorkflow.ParseReview(response);

		Console.WriteLine(decision.Approved
			? $"[Checker] attempt {brief.Attempt}: approved."
			: $"[Checker] attempt {brief.Attempt}: rejected.");
		foreach (var item in decision.Feedback)
		{
			Console.WriteLine($"  - {item}");
		}

		return new FactoryReview(output, decision.Approved, decision.Feedback);
	}
}

/// <summary>
/// The credential-free demo Checker: approves any small change the scripted Maker produced, so the
/// demo exercises the exact Maker -> Checker -> Oracle -> Reporter graph without a model call.
/// </summary>
internal sealed class ScriptedApproverChecker() : Executor<FactoryOutput, FactoryReview>("CleanerChecker")
{
	public override ValueTask<FactoryReview> HandleAsync(
		FactoryOutput output,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var change = output.Change;
		if (change is null)
		{
			return ValueTask.FromResult(new FactoryReview(output, false, ["The Maker produced no change this attempt."]));
		}

		var diffLines = change.Evidence.Split('\n').Length;
		if (diffLines > 40)
		{
			Console.WriteLine($"[Checker] attempt {output.Brief.Attempt}: rejected - diff is {diffLines} lines, larger than a few lines.");
			return ValueTask.FromResult(new FactoryReview(output, false, ["The change is larger than a few lines; keep it minimal."]));
		}

		Console.WriteLine($"[Checker] attempt {output.Brief.Attempt}: approved (scripted).");
		return ValueTask.FromResult(new FactoryReview(output, true, []));
	}
}

internal sealed class ToolCallingMakerExecutor(ChatClient chat, string projectRoot) : Executor<FactoryStep, FactoryOutput>("ToolMaker")
{
	private const int MaxToolRounds = 12;

	public override async ValueTask<FactoryOutput> HandleAsync(
		FactoryStep message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var brief = message.NextAttempt
			?? throw new InvalidOperationException("The maker only runs when the graph supplies a next attempt.");

		var options = new ChatCompletionOptions();
		foreach (var tool in FactoryDevTools.BuildChatTools())
		{
			options.Tools.Add(tool);
		}

		var history = new List<ChatMessage>
		{
			new SystemChatMessage(FactoryLoopWorkflow.BuildMakerInstructions()),
			new UserChatMessage(FactoryLoopWorkflow.BuildMakerPrompt(brief)),
		};

		ChatCompletion? latest = null;
		var written = new List<string>();
		for (var round = 0; round < MaxToolRounds; round++)
		{
			var response = await chat.CompleteChatAsync(history, options, cancellationToken);
			latest = response.Value;
			history.Add(new AssistantChatMessage(latest));

			if (latest.FinishReason != ChatFinishReason.ToolCalls || latest.ToolCalls.Count == 0)
			{
				break;
			}

			foreach (var call in latest.ToolCalls)
			{
				history.Add(new ToolChatMessage(call.Id, await ExecuteToolAsync(call, written)));
			}
		}

		var text = latest?.Content?.FirstOrDefault()?.Text ?? string.Empty;
		var (file, summary) = ParseStructuredOutput(text, written);
		Console.WriteLine($"[Maker] attempt {brief.Attempt}: produced {WorkflowFormat.CountWords(text)} word(s); changed {file ?? "(none)"}.");

		if (string.IsNullOrWhiteSpace(file))
		{
			return new FactoryOutput(null, brief);
		}

		var resolved = ResolveEvidencePath(file);
		var after = FactoryDevTools.ReadFile(resolved, projectRoot);
		return new FactoryOutput(
			new FactoryChange(resolved, summary ?? "Applied a small behavior-preserving change.", BeforeContent: null, AfterContent: after),
			brief);
	}

	private async Task<string> ExecuteToolAsync(ChatToolCall call, List<string> written)
	{
		try
		{
			using var doc = JsonDocument.Parse(call.FunctionArguments);
			var root = doc.RootElement;
			switch (call.FunctionName)
			{
				case "read_file":
					return FactoryLoopWorkflow.Truncate(FactoryDevTools.ReadFile(Get(root, "path"), projectRoot));
				case "write_file":
				{
					var path = Get(root, "path");
					var content = Get(root, "content");
					var result = FactoryDevTools.WriteFile(path, content, projectRoot);
					if (result.StartsWith("Wrote") && !written.Contains(path))
					{
						written.Add(path);
					}

					return result;
				}
				case "run_command":
				{
					var command = Get(root, "command");
					var args = Get(root, "args");
					if (command is not ("dotnet" or "git"))
					{
						return $"(refusing to run '{command}'; only 'dotnet' and 'git' are allowed)";
					}

					var result = await FactoryDevTools.RunCommandAsync(command, args, projectRoot, timeoutSeconds: 180);
					return FactoryLoopWorkflow.Truncate(result.CapturedOutput, 4000);
				}
				case "git_diff":
				{
					var path = Get(root, "path");
					return FactoryLoopWorkflow.Truncate(
						FactoryDevTools.GitDiffWorkingTree(string.IsNullOrWhiteSpace(path) ? null : path, projectRoot),
						4000);
				}
				default:
					return $"(unknown tool '{call.FunctionName}')";
			}
		}
		catch (Exception ex)
		{
			return $"(tool error: {ex.Message})";
		}
	}

	/// <summary>Resolves the model-reported file so git-diff evidence works from the project root.</summary>
	private string ResolveEvidencePath(string file)
	{
		if (Path.IsPathRooted(file))
		{
			return file;
		}

		var direct = Path.Combine(projectRoot, file);
		if (File.Exists(direct))
		{
			return direct;
		}

		var parentRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(projectRoot));
		if (parentRoot is not null)
		{
			var fromParent = Path.Combine(parentRoot, file);
			if (File.Exists(fromParent))
			{
				return fromParent;
			}
		}

		return direct;
	}

	private static string Get(JsonElement root, string property) =>
		root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;

	private static (string? File, string? Summary) ParseStructuredOutput(string text, IReadOnlyList<string> written)
	{
		string? file = null;
		string? summary = null;
		foreach (var rawLine in text.Split('\n'))
		{
			var line = rawLine.Trim();
			if (line.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
			{
				file = line["FILE:".Length..].Trim();
			}
			else if (line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
			{
				summary = line["SUMMARY:".Length..].Trim();
			}
		}

		if (string.IsNullOrWhiteSpace(file))
		{
			file = written.LastOrDefault();
		}

		if (string.IsNullOrWhiteSpace(summary))
		{
			summary = FactoryLoopWorkflow.Truncate(text.Trim(), 200);
		}

		return (file, summary);
	}
}


/// <summary>
/// The deterministic gate of the factory loop. It combines BOTH gates the tutorial demands: the
/// separate Checker's quality verdict (code-review-and-quality) and the machine oracle (`dotnet
/// build`). A change only passes when the Checker approved AND the build exits 0; anything else
/// re-enters the Maker up to the retry cap, then exits with the unresolved report.
/// </summary>
internal sealed class FactoryBuildOracleValidator(string? projectRoot = null) : Executor<FactoryReview, FactoryStep>("BuildOracle")
{
	public override async ValueTask<FactoryStep> HandleAsync(
		FactoryReview review,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var brief = review.Output.Brief;
		var change = review.Output.Change;
		var failures = new List<string>();

		if (change is null)
		{
			failures.Add("The Maker produced no change this attempt.");
		}
		else if (!review.Approved)
		{
			// The separate Checker owns the quality gate until it says the diff is sound.
			failures.AddRange(review.Feedback);
		}

		CommandResult? build = null;
		if (failures.Count == 0)
		{
			// Only spend a build once the Checker approved; the build is the hard-to-fake oracle.
			build = await FactoryDevTools.RunDotNetBuildOracleAsync(projectRoot);
			if (build.ExitCode != 0)
			{
				failures.Add("The project does not build after the change:\n"
					+ FactoryLoopWorkflow.Truncate(FactoryLoopWorkflow.BuildErrors(build.CapturedOutput), 4000));
			}
		}

		if (failures.Count == 0)
		{
			Console.WriteLine($"[Oracle] attempt {brief.Attempt}: checker approved and dotnet build PASSED (exit 0).");
			return FactoryStep.Done(BuildReport(change!, brief.Attempt, build!, passed: true));
		}

		Console.WriteLine($"[Oracle] attempt {brief.Attempt}: {failures.Count} problem(s) found.");
		foreach (var failure in failures)
		{
			Console.WriteLine($"  - {FactoryLoopWorkflow.Truncate(failure, 300)}");
		}

		if (brief.Attempt >= brief.MaxAttempts)
		{
			Console.WriteLine("[Oracle] retry budget spent; exiting with the unresolved report.");
			return FactoryStep.Done(BuildReport(change, brief.Attempt, build, passed: false, failures));
		}

		return FactoryStep.ForAttempt(brief.ForRetry(failures));
	}

	private static string BuildReport(FactoryChange? change, int attempt, CommandResult? build, bool passed, IReadOnlyList<string>? failures = null)
	{
		var changeText = change is null
			? "(no change produced)"
			: $"File: {change.FilePath}\nSummary: {change.Summary}\n\n--- diff ---\n{FactoryLoopWorkflow.Truncate(change.Evidence, 4000)}";

		var validation = passed
			? $"Checker approved and dotnet build passed (exit 0) after {attempt} maker attempt(s)."
			: $"Did not pass within {attempt} attempt(s). Failures:\n{string.Join(Environment.NewLine, failures ?? [])}";

		var log = build is null
			? string.Empty
			: FactoryLoopWorkflow.Truncate(passed ? build.CapturedOutput : FactoryLoopWorkflow.BuildErrors(build.CapturedOutput), 2500);

		return
			$"""
			=== Deliverable ===
			{changeText}

			=== Validation ===
			{validation}
			{log}

			=== Human step (outer loop) ===
			The loop never commits. Review the evidence, then apply/merge or reject the change yourself.
			""";
	}
}

/// <summary>Writes the terminal report into the loop's memory state file so the next run resumes from here.</summary>
internal sealed class FactoryReporterExecutor(string stateFilePath) : Executor<FactoryStep, string>("Reporter")
{
	public override ValueTask<string> HandleAsync(
		FactoryStep message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var report = message.FinalReport ?? "(no report produced)";
		LoopMemory.Append(stateFilePath, report);
		Console.WriteLine($"[Reporter] memory state written to {stateFilePath}");
		return ValueTask.FromResult(report);
	}
}

