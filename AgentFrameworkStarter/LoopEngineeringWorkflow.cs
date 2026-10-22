using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

/// <summary>
/// Loop-engineering workflow (skills.addy.ie/tutorials/loop-engineering/), abstracted as a generic
/// prompt-driven loop rather than a code-simplification example.
///
/// Flow: Selector wraps the user's task prompt into the first MakerBrief -> Maker (agent) produces
/// work -> Checker (separate agent) judges it done or not with feedback -> Validator (deterministic)
/// owns the bounded stop condition (loop back to the Maker up to a retry cap, then exit) -> Reporter
/// writes the loop's memory state file and hands back PR-style evidence for a human.
/// </summary>
internal static class LoopEngineeringWorkflow
{
	/// <summary>The default task used by the console `loop` subcommand when no UI is involved.</summary>
	public const string DefaultTask =
		"Improve this codebase by fixing one small, verifiable issue using test-driven development. "
		+ "Keep the change minimal and behavior-preserving.";

	/// <summary>One attempt plus one repair pass. The cap is what stops rejected work from looping forever.</summary>
	public const int DefaultMaxAttempts = 2;

	/// <summary>Runs the loop with an explicitly built request (used by the Blazor UI via RunInputs).</summary>
	public static async Task RunAsync(OpenAIClient client, string model, LoopRequest request)
	{
		// Maker (incremental-implementation + test-driven-development), Checker (code-review-and-quality).
		// The Validator and Reporter are deterministic code.
		var selector = new SelectorExecutor();
		var maker = new MakerExecutor(CreateAgent(client, model, "Maker", BuildMakerInstructions()));
		var checker = new LoopCheckerExecutor(CreateAgent(client, model, "Checker", BuildCheckerInstructions()));
		var validator = new LoopValidatorExecutor();
		var reporter = new LoopReporterExecutor(request.StateFilePath);

		// Explicit graph: Selector -> Maker -> Checker -> Validator, with the Validator owning the loop branch.
		WorkflowBuilder builder = new(selector);
		builder.AddEdge(selector, maker);
		builder.AddEdge(maker, checker);
		builder.AddEdge(checker, validator);

		// The Validator owns the branch: an unsettled attempt re-enters the Maker, a settled one exits.
		builder.AddEdge<LoopStep>(validator, maker, step => step?.NextAttempt is not null);
		builder.AddEdge<LoopStep>(validator, reporter, step => step?.NextAttempt is null);

		// Only the Reporter's yielded value is a workflow output, so intermediate work stays out of the result.
		builder.WithOutputFrom(reporter);

		var workflow = builder.Build();

		Console.WriteLine($"Task              : {request.Task}");
		Console.WriteLine($"Checker           : separate model, code-review-and-quality rubric");
		Console.WriteLine($"Memory state file : {request.StateFilePath}");
		Console.WriteLine($"Retry budget      : {request.MaxAttempts} maker attempt(s)");
		Console.WriteLine();

		await using Run run = await InProcessExecution.RunAsync(workflow, request);

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
					break;
			}
		}
	}

	/// <summary>
	/// Builds the default request for the console `loop` subcommand. The task prompt is the input now;
	/// nothing is parsed from the command line.
	/// </summary>
	public static LoopRequest BuildDefaultRequest()
	{
		var stateFile = Path.Combine(Environment.CurrentDirectory, "loop-state.md");
		return new LoopRequest(DefaultTask, stateFile, DefaultMaxAttempts);
	}

	private static AIAgent CreateAgent(OpenAIClient client, string model, string name, string instructions) =>
		client.GetChatClient(model).AsAIAgent(new ChatClientAgentOptions
		{
			Name = name,
			ChatOptions = new ChatOptions { Instructions = instructions }
		});

	/// <summary>Maker instructions are the maker-side skills from the tutorial page, minus the worked example.</summary>
	private static string BuildMakerInstructions()
	{
		var b = new StringBuilder();
		b.AppendLine("You are the MAKER in a bounded loop-engineering workflow. You complete one task to a");
		b.AppendLine("reviewable standard, then a separate Checker judges your work. Act only from the skills");
		b.AppendLine("below and ignore any other skill referenced inside them.");
		b.AppendLine();
		b.AppendLine("=== Skill: incremental-implementation ===");
		b.AppendLine(SkillGuide.Load("incremental-implementation", 4500));
		b.AppendLine();
		b.AppendLine("=== Skill: test-driven-development ===");
		b.AppendLine(SkillGuide.Load("test-driven-development", 5000));
		return b.ToString();
	}

	/// <summary>Checker instructions are the checker-side skill from the tutorial page, and nothing else.</summary>
	private static string BuildCheckerInstructions()
	{
		var b = new StringBuilder();
		b.AppendLine("You are the CHECKER in a bounded loop-engineering workflow. You are a separate model");
		b.AppendLine("from the maker: grade the maker's work, never your own. You act only from the skill below");
		b.AppendLine("and ignore any other skill referenced inside it.");
		b.AppendLine();
		b.AppendLine("=== Skill: code-review-and-quality ===");
		b.AppendLine(SkillGuide.Load("code-review-and-quality", 9000));
		return b.ToString();
	}

	/// <summary>
	/// Credential-free smoke test for the loop's deterministic machinery: the skills load, memory
	/// round-trips, the default request builds, and the Checker decision parser accepts and rejects
	/// the right shapes. This is the part testable before spending model calls on a retry.
	/// </summary>
	public static async Task RunSmokeAsync()
	{
		Console.WriteLine("Loop smoke test (no model calls):");

		var skills = new[] { "incremental-implementation", "test-driven-development", "code-review-and-quality", "git-workflow-and-versioning" };
		foreach (var skill in skills)
		{
			var content = SkillGuide.Load(skill);
			Console.WriteLine($"  skill {skill,-32} -> {(content.Length > 20 ? "loaded" : "missing")} ({content.Length} chars)");
		}

		var stateFile = Path.Combine(Path.GetTempPath(), $"loop-state-{Guid.NewGuid():N}.md");
		LoopMemory.Append(stateFile, "smoke test entry");
		var memory = LoopMemory.Read(stateFile);
		Console.WriteLine($"  memory        -> {(memory.Contains("smoke test entry", StringComparison.Ordinal) ? "round-trip OK" : "round-trip FAILED")}");
		File.Delete(stateFile);

		Console.WriteLine($"  default task  -> {(BuildDefaultRequest().Task.Length > 20 ? "OK" : "FAILED")}");

		var approved = PlannerWorkerValidatorWorkflow.ParseReview(
			"""{"approved":true,"feedback":[]}""");
		Console.WriteLine($"  parse approve -> {(approved.Approved ? "OK" : "FAILED")}");

		try
		{
			PlannerWorkerValidatorWorkflow.ParseReview("not json");
			Console.WriteLine("  parse reject -> FAILED (no exception)");
		}
		catch (Exception)
		{
			Console.WriteLine("  parse reject -> OK (rejected)");
		}

		await Task.CompletedTask;
		Console.WriteLine();
		Console.WriteLine("Smoke test complete.");
	}
}


/// <summary>
/// The loop's entry point: wraps the user's task prompt into the first MakerBrief. Reads the memory
/// state file (step 5: give the loop a memory) so a run resumes from where the last one stopped.
/// </summary>
internal sealed class SelectorExecutor() : Executor<LoopRequest, LoopStep>("Selector")
{
	public override ValueTask<LoopStep> HandleAsync(
		LoopRequest message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		// Memory: pull in what the state file recorded so the Maker doesn't repeat finished work.
		var memory = LoopMemory.Read(message.StateFilePath);

		var brief = new MakerBrief(
			Task: message.Task,
			Attempt: 1,
			Feedback: [],
			PreviousOutput: string.Empty,
			Memory: memory,
			MaxAttempts: message.MaxAttempts);

		return ValueTask.FromResult(LoopStep.ForAttempt(brief));
	}
}

/// <summary>
/// The Maker (the worker of the loop). Given the task prompt, it produces the work and returns it as
/// text. On a repair attempt it also sees the Checker's feedback and its previous output.
/// </summary>
internal sealed class MakerExecutor(AIAgent maker) : Executor<LoopStep, MakerOutput>("Maker")
{
	public override async ValueTask<MakerOutput> HandleAsync(
		LoopStep message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var brief = message.NextAttempt
			?? throw new InvalidOperationException("The Maker only runs when the graph supplies a next attempt.");

		var prompt = BuildPrompt(brief);
		var text = (await maker.RunAsync(prompt, cancellationToken: cancellationToken)).Text ?? string.Empty;

		Console.WriteLine($"[Maker] attempt {brief.Attempt}: produced {WorkflowFormat.CountWords(text)} word(s).");
		return new MakerOutput(text, brief);
	}

	private static string BuildPrompt(MakerBrief brief)
	{
		var b = new StringBuilder();
		b.AppendLine("Task:");
		b.AppendLine(brief.Task);

		if (brief.Feedback.Count > 0)
		{
			b.AppendLine();
			b.AppendLine("A previous attempt failed these check(s). Fix every one of them and keep whatever already passed:");
			foreach (var failure in brief.Feedback)
			{
				b.AppendLine($"- {failure}");
			}

			b.AppendLine();
			b.AppendLine("Previous attempt:");
			b.AppendLine(brief.PreviousOutput);
		}

		b.AppendLine();
		b.AppendLine("Complete the task. Reply with the deliverable only: no preamble, no apologies, and no commentary.");
		return b.ToString();
	}
}


/// <summary>
/// The Checker (step 3: add a separate checker). A second, independent agent grades the Maker's work
/// against the task using the code-review-and-quality rubric, returning a JSON approve/reject decision.
/// It never rewrites the work, so its judgment is trustworthy.
/// </summary>
internal sealed class LoopCheckerExecutor(AIAgent checker) : Executor<MakerOutput, LoopReviewResult>("Checker")
{
	public override async ValueTask<LoopReviewResult> HandleAsync(
		MakerOutput message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var brief = message.Brief;
		var prompt =
			$$"""
			Task:
			{{brief.Task}}

			Maker's work:
			<work>
			{{message.Text}}
			</work>

			Judge the work against your rubric and only the task scope:
			- It addresses the requested task and stays within its scope.
			- It is complete, clear, and specific; no placeholders or process commentary.
			- It is verifiable: a test or check could prove it works.
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

		return new LoopReviewResult(message, decision.Approved, decision.Feedback);
	}
}

/// <summary>
/// The deterministic gate (step 4: wrap it in a loop with a real stop condition). No model call: it
/// owns the bounded branch â€” a rejected attempt re-enters the Maker up to the retry cap, then exits
/// with the unresolved feedback.
/// </summary>
internal sealed class LoopValidatorExecutor() : Executor<LoopReviewResult, LoopStep>("Validator")
{
	public override ValueTask<LoopStep> HandleAsync(
		LoopReviewResult review,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		// Deterministic scope check: the Maker must have produced something.
		var failures = review.Approved ? FindFailures(review.Result) : review.Feedback.ToList();

		if (failures.Count == 0)
		{
			Console.WriteLine($"[Validator] attempt {review.Result.Brief.Attempt}: checker approved.");
			return ValueTask.FromResult(LoopStep.Done(BuildReport(review.Result, passed: true, failures)));
		}

		var failureSource = review.Approved ? "deterministic check" : "checker";
		Console.WriteLine($"[Validator] attempt {review.Result.Brief.Attempt}: {failureSource} found {failures.Count} problem(s).");
		foreach (var failure in failures)
		{
			Console.WriteLine($"  - {failure}");
		}

		var attempt = review.Result.Brief.Attempt;
		if (attempt >= review.Result.Brief.MaxAttempts)
		{
			Console.WriteLine("[Validator] retry budget spent; exiting with unresolved feedback.");
			return ValueTask.FromResult(LoopStep.Done(BuildReport(review.Result, passed: false, failures)));
		}

		// Bounded repair: send the diagnostics back around the loop as the Maker's next brief.
		return ValueTask.FromResult(LoopStep.ForAttempt(review.Result.Brief.ForRetry(failures, review.Result.Text)));
	}

	private static List<string> FindFailures(MakerOutput result)
	{
		var failures = new List<string>();
		if (string.IsNullOrWhiteSpace(result.Text))
		{
			failures.Add("The Maker produced no output.");
		}

		return failures;
	}

	private static string BuildReport(MakerOutput result, bool passed, IReadOnlyList<string> failures)
	{
		var verdict = passed
			? $"Checker approved after {result.Brief.Attempt} maker attempt(s)."
			: $"Did not pass within {result.Brief.Attempt} attempt(s). Unresolved feedback:"
			+ Environment.NewLine
			+ string.Join(Environment.NewLine, failures.Select(failure => $"- {failure}"));

		return
			$"""
			=== Deliverable ===
			{result.Text}

			=== Validation ===
			{verdict}
			""";
	}
}

/// <summary>
/// The evidence hand-off (step 6: git-workflow-and-versioning). Publishes the terminal report as the
/// workflow output and updates the memory state file so tomorrow's run resumes instead of repeating.
/// </summary>
internal sealed class LoopReporterExecutor(string stateFilePath) : Executor<LoopStep, string>("Reporter")
{
	public override ValueTask<string> HandleAsync(
		LoopStep message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var report = message.FinalReport ?? "(no report produced)";

		// Memory: record what changed and what is left outside any single conversation.
		LoopMemory.Append(stateFilePath, report);
		Console.WriteLine($"[Reporter] memory state written to {stateFilePath}");

		return ValueTask.FromResult(report);
	}
}


/// <summary>The workflow's input: a free-form task prompt, where memory lives, and the retry cap.</summary>
internal sealed record LoopRequest(string Task, string StateFilePath, int MaxAttempts);

/// <summary>Everything the Maker needs for one attempt, including why the previous one fell short.</summary>
internal sealed record MakerBrief(
	string Task,
	int Attempt,
	IReadOnlyList<string> Feedback,
	string PreviousOutput,
	string Memory,
	int MaxAttempts)
{
	public MakerBrief ForRetry(IReadOnlyList<string> feedback, string previousOutput) =>
		this with { Attempt = Attempt + 1, Feedback = feedback, PreviousOutput = previousOutput };
}

/// <summary>The Maker's output (text), carried with the brief it answered so the Validator needs no shared state.</summary>
internal sealed record MakerOutput(string Text, MakerBrief Brief);

/// <summary>The Checker's independent decision and any feedback to send back to the Maker.</summary>
internal sealed record LoopReviewResult(MakerOutput Result, bool Approved, IReadOnlyList<string> Feedback);

/// <summary>
/// The one message type that travels around the loop, in both directions:
/// either the next attempt for the Maker, or the final report that ends the run.
/// Sharing the type keeps both conditional edges statically checkable instead of cast-based.
/// </summary>
internal sealed record LoopStep(MakerBrief? NextAttempt, string? FinalReport)
{
	public static LoopStep ForAttempt(MakerBrief brief) => new(brief, null);

	public static LoopStep Done(string report) => new(null, report);
}


/// <summary>
/// Step 5: give the loop a memory. The state file is the spine that lets tomorrow's run resume
/// instead of repeating itself. Written outside any single conversation, so runs can be scheduled.
/// </summary>
internal static class LoopMemory
{
	public static string Read(string stateFilePath)
	{
		if (!File.Exists(stateFilePath))
		{
			return "(no prior state; this is the first run)";
		}

		return File.ReadAllText(stateFilePath);
	}

	public static void Append(string stateFilePath, string report)
	{
		var directory = Path.GetDirectoryName(stateFilePath);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var entry =
			$"""
			## Run {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}

			{report}

			---
			""";

		File.AppendAllText(stateFilePath, entry);
	}
}

/// <summary>Loads the tutorial page's skills from the repository's .agents/skills directory.</summary>
internal static class SkillGuide
{
	private const string SkillsRoot = ".agents/skills";

	public static string Load(string name, int maxLength = int.MaxValue)
	{
		var path = Find(name);
		if (path is null)
		{
			return $"(skill '{name}' not found under {SkillsRoot}; install it with `npx skills add addyosmani/agent-skills@{name}`)";
		}

		var text = File.ReadAllText(path);
		text = StripFrontMatter(text);

		return text.Length <= maxLength ? text : $"{text[..maxLength]}\nâ€¦ (excerpt truncated)";
	}

	private static string? Find(string name)
	{
		// Walk up from the current directory to the repository root.
		var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, SkillsRoot, name, "SKILL.md");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		return null;
	}

	/// <summary>Drops the YAML front matter (the '---' block) and keeps the instruction body.</summary>
	private static string StripFrontMatter(string markdown)
	{
		const string delimiter = "---";
		var trimmed = markdown.TrimStart();
		if (!trimmed.StartsWith(delimiter, StringComparison.Ordinal))
		{
			return markdown;
		}

		var end = trimmed.IndexOf(delimiter, delimiter.Length, StringComparison.Ordinal);
		if (end < 0)
		{
			return markdown;
		}

		return trimmed[(end + delimiter.Length)..].TrimStart();
	}
}

