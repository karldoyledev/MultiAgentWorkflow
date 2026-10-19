using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

/// <summary>
/// The course project's explicit workflow graph: Planner (agent) -> Worker (agent) -> Validator (deterministic code),
/// with failed checks routed back to the Worker and a retry cap that keeps the loop bounded.
/// Only the Planner and the Worker spend model calls; the Validator is ordinary, testable code.
/// </summary>
internal static class PlannerWorkerValidatorWorkflow
{
	/// <summary>One draft plus one repair pass. The cap is what stops a failing draft from looping forever.</summary>
	public const int MaxWorkerAttempts = 2;

	public static async Task RunAsync(OpenAIClient client, string model, IReadOnlyList<string> taskArguments)
	{
		var request = BuildRequest(taskArguments);

		var planner = new PlannerExecutor(CreateAgent(client, model, "Planner", """
			You are a planner for a small technical writing team.
			Break the requested deliverable into a short numbered outline of sections.
			Reply with the outline only: no preamble and no commentary.
			"""));

		var worker = new WorkerExecutor(CreateAgent(client, model, "Worker", """
			You are a technical writer producing a short release note.
			Use the planner's section titles as headings, in the planner's order.
			If repair feedback is supplied, fix every listed problem and keep whatever already passed.
			Reply with the deliverable only: no preamble, no apologies, and no commentary about the process.
			"""));

		var validator = new ValidatorExecutor();
		var reporter = new ReporterExecutor();

		// The graph is a straight line with one branch back to the Worker.
		WorkflowBuilder builder = new(planner);
		builder.AddEdge(planner, worker);
		builder.AddEdge(worker, validator);

		// The Validator owns the branch: an unsettled attempt re-enters the Worker, a settled one exits.
		builder.AddEdge<NextStep>(validator, worker, step => step?.NextAttempt is not null);
		builder.AddEdge<NextStep>(validator, reporter, step => step?.NextAttempt is null);

		// Only the Reporter's yielded value is a workflow output, so intermediate drafts stay out of the result.
		builder.WithOutputFrom(reporter);

		var workflow = builder.Build();

		Console.WriteLine($"Task              : {request.Task}");
		Console.WriteLine($"Required sections : {string.Join(", ", request.RequiredSections)}");
		Console.WriteLine($"Retry budget      : {MaxWorkerAttempts} worker attempt(s)");
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

	private static WorkflowRequest BuildRequest(IReadOnlyList<string> taskArguments) =>
		taskArguments.Count == 0
			? new WorkflowRequest(
				"Write a short release note for the new session persistence feature.",
				["Summary", "Changes", "Risks"])
			: new WorkflowRequest(string.Join(' ', taskArguments), ["Summary", "Changes", "Risks"]);

	private static AIAgent CreateAgent(OpenAIClient client, string model, string name, string instructions) =>
		client.GetChatClient(model).AsAIAgent(new ChatClientAgentOptions
		{
			Name = name,
			ChatOptions = new ChatOptions { Instructions = instructions }
		});
}

/// <summary>Turns the raw task into the first brief for the Worker: a model call, then a typed message.</summary>
internal sealed class PlannerExecutor(AIAgent planner) : Executor<WorkflowRequest, NextStep>("Planner")
{
	public override async ValueTask<NextStep> HandleAsync(
		WorkflowRequest message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var prompt =
			$"""
			Task: {message.Task}

			The deliverable must contain exactly these sections: {string.Join(", ", message.RequiredSections)}.
			Return a numbered outline that uses those section titles.
			""";

		// The planner agent has no session of its own: its context is exactly the prompt built here.
		var outline = (await planner.RunAsync(prompt, cancellationToken: cancellationToken)).Text ?? string.Empty;

		Console.WriteLine("[Planner] outline");
		Console.WriteLine(WorkflowFormat.Indent(outline));
		Console.WriteLine();

		var brief = new AttemptBrief(
			Task: message.Task,
			Outline: outline,
			RequiredSections: message.RequiredSections,
			Attempt: 1,
			Feedback: [],
			PreviousDraft: string.Empty);

		return NextStep.ForAttempt(brief);
	}
}

/// <summary>Writes one draft attempt, on the first pass or in response to repair feedback.</summary>
internal sealed class WorkerExecutor(AIAgent worker) : Executor<NextStep, Draft>("Worker")
{
	public override async ValueTask<Draft> HandleAsync(
		NextStep message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var brief = message.NextAttempt
			?? throw new InvalidOperationException("The Worker only runs when the graph supplies a next attempt.");

		var prompt =
			$"""
			Task: {brief.Task}

			Planner outline:
			{brief.Outline}
			{WorkflowFormat.RepairSection(brief)}
			Write the deliverable now.
			""";

		var text = (await worker.RunAsync(prompt, cancellationToken: cancellationToken)).Text ?? string.Empty;

		Console.WriteLine($"[Worker] attempt {brief.Attempt}: wrote {WorkflowFormat.CountWords(text)} word(s).");
		return new Draft(text, brief);
	}
}

/// <summary>
/// The deterministic gate. No model call: these checks either pass or produce the exact diagnostics to repair,
/// which is what makes the workflow's control path testable before spending model calls on a retry.
/// </summary>
internal sealed class ValidatorExecutor() : Executor<Draft, NextStep>("Validator")
{
	private const int MinimumWords = 40;

	private static readonly string[] ForbiddenPhrases =
		["as an ai", "i cannot", "lorem ipsum", "todo", "placeholder"];

	public override ValueTask<NextStep> HandleAsync(
		Draft message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var attempt = message.Brief.Attempt;
		var failures = FindFailures(message);

		if (failures.Count == 0)
		{
			Console.WriteLine($"[Validator] attempt {attempt}: passed.");
			return ValueTask.FromResult(NextStep.Done(BuildReport(message, attempt, passed: true, failures)));
		}

		Console.WriteLine($"[Validator] attempt {attempt}: {failures.Count} problem(s) found.");
		foreach (var failure in failures)
		{
			Console.WriteLine($"  - {failure}");
		}

		if (attempt >= PlannerWorkerValidatorWorkflow.MaxWorkerAttempts)
		{
			Console.WriteLine("[Validator] retry budget spent; exiting with unresolved checks.");
			return ValueTask.FromResult(NextStep.Done(BuildReport(message, attempt, passed: false, failures)));
		}

		// Bounded repair: send the diagnostics back around the loop as the Worker's next brief.
		return ValueTask.FromResult(NextStep.ForAttempt(message.Brief.ForRetry(failures, message.Text)));
	}

	private static List<string> FindFailures(Draft draft)
	{
		var failures = new List<string>();
		var text = draft.Text;

		if (string.IsNullOrWhiteSpace(text))
		{
			failures.Add("The draft is empty.");
			return failures;
		}

		var wordCount = WorkflowFormat.CountWords(text);
		if (wordCount < MinimumWords)
		{
			failures.Add($"The draft has {wordCount} word(s); at least {MinimumWords} are required.");
		}

		foreach (var section in draft.Brief.RequiredSections)
		{
			if (!text.Contains(section, StringComparison.OrdinalIgnoreCase))
			{
				failures.Add($"Missing required section heading: {section}.");
			}
		}

		var lowered = text.ToLowerInvariant();
		foreach (var phrase in ForbiddenPhrases)
		{
			if (lowered.Contains(phrase, StringComparison.Ordinal))
			{
				failures.Add($"Remove the forbidden phrase \"{phrase}\".");
			}
		}

		return failures;
	}

	private static string BuildReport(Draft draft, int attempts, bool passed, IReadOnlyList<string> failures)
	{
		var verdict = passed
			? $"Passed all checks after {attempts} worker attempt(s)."
			: $"Did not pass within {attempts} worker attempt(s). Unresolved checks:"
			+ Environment.NewLine
			+ string.Join(Environment.NewLine, failures.Select(failure => $"- {failure}"));

		return
			$"""
			=== Deliverable ===
			{draft.Text}

			=== Validation ===
			{verdict}
			""";
	}
}

/// <summary>Publishes the terminal report as the workflow output.</summary>
internal sealed class ReporterExecutor() : Executor<NextStep, string>("Reporter")
{
	public override ValueTask<string> HandleAsync(
		NextStep message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
		=> ValueTask.FromResult(message.FinalReport ?? "(no report produced)");
}

/// <summary>The workflow's input: what to write, and which sections a reviewer will look for.</summary>
internal sealed record WorkflowRequest(string Task, IReadOnlyList<string> RequiredSections);

/// <summary>Everything the Worker needs for a single attempt, including why the previous one fell short.</summary>
internal sealed record AttemptBrief(
	string Task,
	string Outline,
	IReadOnlyList<string> RequiredSections,
	int Attempt,
	IReadOnlyList<string> Feedback,
	string PreviousDraft)
{
	public AttemptBrief ForRetry(IReadOnlyList<string> feedback, string previousDraft) =>
		this with { Attempt = Attempt + 1, Feedback = feedback, PreviousDraft = previousDraft };
}

/// <summary>The Worker's output, carried with the brief it answered so the Validator needs no shared state.</summary>
internal sealed record Draft(string Text, AttemptBrief Brief);

/// <summary>
/// The one message type that travels around the loop, in both directions:
/// either the next attempt for the Worker, or the final report that ends the run.
/// Sharing the type keeps both conditional edges statically checkable instead of cast-based.
/// </summary>
internal sealed record NextStep(AttemptBrief? NextAttempt, string? FinalReport)
{
	public static NextStep ForAttempt(AttemptBrief brief) => new(brief, null);

	public static NextStep Done(string report) => new(null, report);
}

/// <summary>Small text helpers shared by the executors.</summary>
internal static class WorkflowFormat
{
	public static int CountWords(string text) =>
		text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

	public static string Indent(string text) =>
		string.Join(
			Environment.NewLine,
			text.Split('\n').Select(line => $"  {line.TrimEnd()}"));

	public static string Preview(string text, int maxLength = 60)
	{
		var singleLine = text.ReplaceLineEndings(" ").Trim();
		return singleLine.Length <= maxLength ? singleLine : $"{singleLine[..maxLength]}...";
	}

	/// <summary>Builds the repair instructions appended to the Worker's prompt after a failed validation.</summary>
	public static string RepairSection(AttemptBrief brief)
	{
		if (brief.Feedback.Count == 0)
		{
			return string.Empty;
		}

		var fixes = string.Join(Environment.NewLine, brief.Feedback.Select(failure => $"- {failure}"));

		return
			$"""
			A previous draft failed these check(s). Fix every one of them:
			{fixes}

			Previous draft:
			{brief.PreviousDraft}
			""";
	}
}
