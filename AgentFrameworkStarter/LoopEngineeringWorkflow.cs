using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

/// <summary>
/// Loop-engineering workflow (skills.addy.ie/tutorials/loop-engineering/).
/// The smallest honest software factory: a Maker proposes one tiny code simplification, a separate
/// Checker reviews it, and a deterministic Validator (the green-or-red oracle) owns the branch:
/// a failed attempt re-enters the Maker up to a bounded retry cap, a settled attempt exits to the
/// Reporter, which writes the loop's memory state file and hands back PR-style evidence for a human.
/// Only the five skills named on the tutorial page are used to instruct the agents.
/// </summary>
internal static class LoopEngineeringWorkflow
{
	/// <summary>One proposal plus one repair pass. The cap is what stops rejected work from looping forever.</summary>
	public const int DefaultMaxAttempts = 2;

	public static async Task RunAsync(OpenAIClient client, string model, IReadOnlyList<string> taskArguments)
		=> await RunAsync(client, model, BuildRequest(taskArguments));

	/// <summary>Runs the loop with an explicitly built request (used by the Blazor UI and the console).</summary>
	public static async Task RunAsync(OpenAIClient client, string model, LoopRequest request)
	{
		// Maker (incremental-implementation + test-driven-development + code-simplification),
		// Checker (code-review-and-quality). The Reporter and Validator are deterministic code.
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

		// Only the Reporter's yielded value is a workflow output, so intermediate proposals stay out of the result.
		builder.WithOutputFrom(reporter);

		var workflow = builder.Build();

		Console.WriteLine($"Pattern           : {request.TargetPattern}");
		Console.WriteLine($"Target file       : {request.TargetFilePath}");
		Console.WriteLine($"Checker           : separate model, code-review-and-quality rubric");
		Console.WriteLine($"Oracle            : dotnet build/test on the target repo");
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

	private static LoopRequest BuildRequest(IReadOnlyList<string> args)
	{
		const string defaultPattern =
			"a verbose or nested conditional that can be expressed more clearly without changing behavior";

		var targetFile = defaultFile ?? string.Empty;
		var pattern = defaultPattern;

		if (args.Count > 0 && File.Exists(args[0]))
		{
			targetFile = Path.GetFullPath(args[0]);
			if (args.Count > 1)
			{
				pattern = string.Join(' ', args.Skip(1));
			}
		}
		else if (args.Count > 0)
		{
			pattern = string.Join(' ', args);
		}

		var root = ProjectRoot.Find(targetFile);
		return new LoopRequest(
			TargetPattern: pattern,
			TargetFilePath: targetFile,
			StateFilePath: Path.Combine(root, "loop-state.md"),
			MaxAttempts: DefaultMaxAttempts);
	}

	private static readonly string? defaultFile = FindDefaultTargetFile();

	/// <summary>Locates the course project's workflow file from any launch directory.</summary>
	internal static string? FindDefaultTargetFile()
	{
		var directory = new DirectoryInfo(Environment.CurrentDirectory);
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "AgentFrameworkStarter", "PlannerWorkerValidatorWorkflow.cs");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		return null;
	}

	private static AIAgent CreateAgent(OpenAIClient client, string model, string name, string instructions) =>
		client.GetChatClient(model).AsAIAgent(new ChatClientAgentOptions
		{
			Name = name,
			ChatOptions = new ChatOptions { Instructions = instructions }
		});

	/// <summary>Maker instructions are the maker-side skills from the tutorial page, and nothing else.</summary>
	private static string BuildMakerInstructions()
	{
		var b = new StringBuilder();
		b.AppendLine("You are the MAKER in a bounded code-simplification loop (loop-engineering).");
		b.AppendLine("You act from the skills below and ignore any other skill referenced inside them.");
		b.AppendLine();
		b.AppendLine("=== Skill: code-simplification ===");
		b.AppendLine(SkillGuide.Load("code-simplification"));
		b.AppendLine();
		b.AppendLine("=== Skill: incremental-implementation (excerpt) ===");
		b.AppendLine(SkillGuide.Load("incremental-implementation", 4500));
		b.AppendLine();
		b.AppendLine("=== Skill: test-driven-development (excerpt) ===");
		b.AppendLine(SkillGuide.Load("test-driven-development", 5000));
		return b.ToString();
	}

	/// <summary>Checker instructions are the checker-side skill from the tutorial page, and nothing else.</summary>
	private static string BuildCheckerInstructions()
	{
		var b = new StringBuilder();
		b.AppendLine("You are the CHECKER in a bounded code-simplification loop (loop-engineering).");
		b.AppendLine("You are a separate model from the maker: grade the maker's work, never your own.");
		b.AppendLine("You act from the skill below and ignore any other skill referenced inside it.");
		b.AppendLine();
		b.AppendLine("=== Skill: code-review-and-quality ===");
		b.AppendLine(SkillGuide.Load("code-review-and-quality", 9000));
		return b.ToString();
	}

	/// <summary>
	/// Credential-free smoke test for the loop's deterministic machinery: the skills load, the oracle
	/// runs, memory round-trips, and proposal parsing accepts well-formed JSON and rejects bad JSON.
	/// This is the part that is testable before spending model calls on a retry.
	/// </summary>
	public static async Task RunSmokeAsync()
	{
		Console.WriteLine("Loop smoke test (no model calls):");

		var skills = new[] { "code-simplification", "incremental-implementation", "test-driven-development", "code-review-and-quality", "git-workflow-and-versioning" };
		foreach (var skill in skills)
		{
			var content = SkillGuide.Load(skill);
			Console.WriteLine($"  skill {skill,-32} -> {(content.Length > 20 ? "loaded" : "missing")} ({content.Length} chars)");
		}

		var root = ProjectRoot.Find(defaultFile ?? Directory.GetCurrentDirectory());
		Console.WriteLine($"  project root  -> {root}");

		var oracle = BuildOracle.Run(root);
		Console.WriteLine($"  oracle        -> {(oracle.Passed ? "PASS" : "FAIL")} ({oracle.Summary})");

		var stateFile = Path.Combine(Path.GetTempPath(), $"loop-state-{Guid.NewGuid():N}.md");
		LoopMemory.Append(stateFile, "smoke test entry");
		var memory = LoopMemory.Read(stateFile);
		Console.WriteLine($"  memory        -> {(memory.Contains("smoke test entry", StringComparison.Ordinal) ? "round-trip OK" : "round-trip FAILED")}");
		File.Delete(stateFile);

		var brief = new MakerBrief(
			TargetPattern: "test",
			TargetFilePath: "test.cs",
			SourceCode: "int x = 1;",
			Attempt: 1,
			Feedback: [],
			PreviousProposal: string.Empty,
			Memory: string.Empty,
			MaxAttempts: 2);

		var parsed = MakerExecutor.ParseProposal(
			"""{"filePath":"test.cs","before":"a","after":"b","rationale":"r","test":"t"}""",
			brief);
		Console.WriteLine($"  parse good    -> {(parsed.Before == "a" && parsed.ParseError.Length == 0 ? "OK" : "FAILED")}");

		var broken = MakerExecutor.ParseProposal("not json", brief);
		Console.WriteLine($"  parse bad     -> {(broken.ParseError.Length > 0 ? "OK (flagged for repair)" : "FAILED (no ParseError)")}");

		await Task.CompletedTask;
		Console.WriteLine();
		Console.WriteLine("Smoke test complete.");
	}
}


/// <summary>
/// The loop's entry point. Reads the memory state file (step 5: give the loop a memory) so a run
/// resumes from where the last one stopped, then hands the Maker its first brief.
/// </summary>
internal sealed class SelectorExecutor() : Executor<LoopRequest, LoopStep>("Selector")
{
	public override ValueTask<LoopStep> HandleAsync(
		LoopRequest message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var source = File.Exists(message.TargetFilePath)
			? File.ReadAllText(message.TargetFilePath)
			: string.Empty;

		// Memory: pull in what the state file recorded so the Maker doesn't repeat finished work.
		var memory = LoopMemory.Read(message.StateFilePath);

		var brief = new MakerBrief(
			TargetPattern: message.TargetPattern,
			TargetFilePath: message.TargetFilePath,
			SourceCode: source,
			Attempt: 1,
			Feedback: [],
			PreviousProposal: string.Empty,
			Memory: memory,
			MaxAttempts: message.MaxAttempts);

		return ValueTask.FromResult(LoopStep.ForAttempt(brief));
	}
}

/// <summary>
/// The Maker (step 2: define the maker). Finds ONE instance of the target pattern in the file and
/// proposes a small, behavior-preserving simplification plus the test that would prove it, following
/// the incremental-implementation and test-driven-development disciplines from its instructions.
/// </summary>
internal sealed class MakerExecutor(AIAgent maker) : Executor<LoopStep, ProposedChange>("Maker")
{
	/// <summary>Cap for the file source embedded in the prompt so large targets stay inside the model's context window.</summary>
	private const int MaxSourceChars = 12_000;

	public override async ValueTask<ProposedChange> HandleAsync(
		LoopStep message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var brief = message.NextAttempt
			?? throw new InvalidOperationException("The Maker only runs when the graph supplies a next attempt.");

		var prompt = BuildPrompt(brief);
		var response = (await maker.RunAsync(prompt, cancellationToken: cancellationToken)).Text
			?? throw new InvalidOperationException("The Maker returned no proposal.");

		Console.WriteLine($"[Maker] attempt {brief.Attempt}: returned {WorkflowFormat.CountWords(response)} word(s).");
		return ParseProposal(response, brief);
	}

	private static string BuildPrompt(MakerBrief brief)
	{
		var b = new StringBuilder();
		b.AppendLine($"Target pattern to find ONE instance of: {brief.TargetPattern}");
		b.AppendLine($"Target file: {brief.TargetFilePath}");
		b.AppendLine();
		b.AppendLine("File content:");
		b.AppendLine("<file>");
		b.AppendLine(TruncateSource(brief.SourceCode));
		b.AppendLine("</file>");

		if (brief.Feedback.Count > 0)
		{
			b.AppendLine();
			b.AppendLine("A previous proposal failed these check(s). Fix every one of them and keep whatever already passed:");
			foreach (var failure in brief.Feedback)
			{
				b.AppendLine($"- {failure}");
			}

			b.AppendLine();
			b.AppendLine("Previous proposal:");
			b.AppendLine(brief.PreviousProposal);
		}

		b.AppendLine();
		b.AppendLine("Find exactly ONE instance of the target pattern and propose a small, behavior-preserving simplification of it.");
		b.AppendLine("Keep the change to a few lines and do not touch anything else (incremental-implementation skill).");
		b.AppendLine("Describe the test that would prove the change preserves behavior (test-driven-development skill).");
		b.AppendLine("Return ONLY a JSON object with this exact shape:");
		b.AppendLine("{\"filePath\":\"<path>\",\"before\":\"<exact current code>\",\"after\":\"<proposed code>\",\"rationale\":\"<one line>\",\"test\":\"<test description>\"}");
		return b.ToString();
	}

	/// <summary>Keeps oversized file sources out of the prompt, preserving the head where patterns usually live.</summary>
	private static string TruncateSource(string source)
	{
		if (source.Length <= MaxSourceChars)
		{
			return source;
		}

		return source[..MaxSourceChars]
			+ Environment.NewLine
			+ $"… (file truncated at {MaxSourceChars} chars; {source.Length - MaxSourceChars} chars not shown)";
	}

	internal static ProposedChange ParseProposal(string response, MakerBrief brief)
	{
		try
		{
			using var document = JsonDocument.Parse(response);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return Broken(response, brief, "The Maker response must be a JSON object.");
			}

			return new ProposedChange(
				FilePath: GetString(root, "filePath"),
				Before: GetString(root, "before"),
				After: GetString(root, "after"),
				Rationale: GetString(root, "rationale"),
				Test: GetString(root, "test"),
				Brief: brief);
		}
		catch (JsonException)
		{
			// Not JSON at all: hand the raw text back to the loop for one repair attempt.
			return Broken(response, brief, "The Maker response must be valid JSON.");
		}
		catch (InvalidOperationException ex)
		{
			// Well-formed JSON but missing/blank required fields (raised by GetString).
			return Broken(response, brief, ex.Message);
		}
	}

	/// <summary>
	/// A malformed response becomes a well-formed-but-empty proposal tagged with the parse problem,
	/// so the Validator's deterministic checks can route it back to the Maker instead of crashing the run.
	/// </summary>
	private static ProposedChange Broken(string response, MakerBrief brief, string reason) =>
		new(
			FilePath: string.Empty,
			Before: string.Empty,
			After: string.Empty,
			Rationale: string.Empty,
			Test: string.Empty,
			Brief: brief,
			ParseError: $"{reason} Raw response: {response}");

	private static string GetString(JsonElement root, string property)
	{
		if (!root.TryGetProperty(property, out var element)
			|| element.ValueKind != JsonValueKind.String
			|| string.IsNullOrWhiteSpace(element.GetString()))
		{
			throw new InvalidOperationException($"The Maker response must include a non-empty string '{property}'.");
		}

		return element.GetString()!;
	}
}

/// <summary>
/// The Checker (step 3: add a separate checker). A second, independent agent grades the Maker's
/// proposal with the code-review-and-quality skill: reject anything outside the target pattern or
/// larger than a few lines. It never rewrites the proposal, so its approval is trustworthy.
/// </summary>
internal sealed class LoopCheckerExecutor(AIAgent checker) : Executor<ProposedChange, LoopReviewResult>("Checker")
{
	public override async ValueTask<LoopReviewResult> HandleAsync(
		ProposedChange message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var brief = message.Brief;
		var prompt =
			$$"""
			Target pattern: {{brief.TargetPattern}}

			Target file: {{brief.TargetFilePath}}

			Maker proposal:
			<proposal>
			{{message}}
			</proposal>

			Review the proposal against your rubric. Judge it only against the target pattern and scope:
			- It addresses the requested pattern and stays in scope (no unrelated sections or files).
			- It is small (a few lines) and behavior-preserving; reject anything larger.
			- It follows the project's existing conventions.
			- It describes a test that would prove the change preserves behavior.
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
/// The deterministic gate (step 4: wrap it in a loop with a real stop condition). No model call:
/// it runs the green-or-red oracle (dotnet build/test) and deterministic scope checks, then decides
/// whether the proposal can exit or must loop back to the Maker, bounded by the retry cap.
/// </summary>
internal sealed class LoopValidatorExecutor() : Executor<LoopReviewResult, LoopStep>("Validator")
{
	private const int MaxProposalLines = 12;

	public override ValueTask<LoopStep> HandleAsync(
		LoopReviewResult review,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
	{
		var proposal = review.Proposal;
		var brief = proposal.Brief;
		var attempt = brief.Attempt;
		var failures = review.Approved ? FindFailures(proposal) : review.Feedback.ToList();

		// The oracle only runs when the checker approved AND the proposal is structurally sound:
		// that is the cheap, hard-to-fake gate that earns the loop its autonomy.
		if (failures.Count == 0)
		{
			var oracle = BuildOracle.Run(ProjectRoot.Find(brief.TargetFilePath));
			if (oracle.Passed)
			{
				Console.WriteLine($"[Validator] attempt {attempt}: checker approved, deterministic checks and oracle passed.");
				return ValueTask.FromResult(LoopStep.Done(BuildReport(proposal, passed: true, oracle, failures)));
			}

			failures.Add($"The repo oracle failed: {oracle.Summary}");
		}

		var failureSource = review.Approved ? "deterministic validation / oracle" : "checker";
		Console.WriteLine($"[Validator] attempt {attempt}: {failureSource} found {failures.Count} problem(s).");
		foreach (var failure in failures)
		{
			Console.WriteLine($"  - {failure}");
		}

		if (attempt >= brief.MaxAttempts)
		{
			Console.WriteLine("[Validator] retry budget spent; exiting with unresolved feedback.");
			return ValueTask.FromResult(LoopStep.Done(BuildReport(
				proposal,
				passed: false,
				oracle: null,
				failures)));
		}

		// Bounded repair: send the diagnostics back around the loop as the Maker's next brief.
		return ValueTask.FromResult(LoopStep.ForAttempt(brief.ForRetry(failures, proposal.ToString())));
	}

	private static List<string> FindFailures(ProposedChange proposal)
	{
		var failures = new List<string>();

		// A malformed Maker response is itself a repair target: hand the parse problem back so the
		// Maker can fix its output format on the next attempt instead of the run silently passing.
		if (proposal.ParseError.Length > 0)
		{
			failures.Add(proposal.ParseError);
			return failures;
		}

		if (string.IsNullOrWhiteSpace(proposal.Before) || string.IsNullOrWhiteSpace(proposal.After))
		{
			failures.Add("The proposal is missing a before or after snippet.");
			return failures;
		}

		// The 'before' must exist verbatim in the target file, or the Maker is inventing code.
		var source = File.Exists(proposal.Brief.TargetFilePath)
			? File.ReadAllText(proposal.Brief.TargetFilePath)
			: string.Empty;
		if (!source.Contains(proposal.Before, StringComparison.Ordinal))
		{
			failures.Add("The 'before' snippet does not appear verbatim in the target file.");
		}

		// The change must stay small: a few lines, one instance, not a rewrite.
		var afterLines = proposal.After.Split('\n').Length;
		if (afterLines > MaxProposalLines)
		{
			failures.Add($"The proposal is {afterLines} line(s); keep changes to a few lines.");
		}

		if (string.IsNullOrWhiteSpace(proposal.Rationale))
		{
			failures.Add("The proposal needs a one-line rationale.");
		}

		if (string.IsNullOrWhiteSpace(proposal.Test))
		{
			failures.Add("The proposal needs a test description that proves the change preserves behavior.");
		}

		return failures;
	}

	private static string BuildReport(
		ProposedChange proposal,
		bool passed,
		BuildOracleResult? oracle,
		IReadOnlyList<string> failures)
	{
		var verdict = passed
			? $"Checker approved and deterministic validation passed. Oracle: {oracle?.Summary ?? "n/a"}."
			: "Did not pass within the retry budget. Unresolved feedback:"
			+ Environment.NewLine
			+ string.Join(Environment.NewLine, failures.Select(failure => $"- {failure}"));

		return
			$"""
			=== Proposed change ===
			{proposal}

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

/// <summary>The workflow's input: what to simplify, where, and how many repair attempts are allowed.</summary>
internal sealed record LoopRequest(
	string TargetPattern,
	string TargetFilePath,
	string StateFilePath,
	int MaxAttempts);

/// <summary>Everything the Maker needs for a single attempt, plus the loop's memory so it resumes work.</summary>
internal sealed record MakerBrief(
	string TargetPattern,
	string TargetFilePath,
	string SourceCode,
	int Attempt,
	IReadOnlyList<string> Feedback,
	string PreviousProposal,
	string Memory,
	int MaxAttempts)
{
	public MakerBrief ForRetry(IReadOnlyList<string> feedback, string previousProposal) =>
		this with { Attempt = Attempt + 1, Feedback = feedback, PreviousProposal = previousProposal };
}

/// <summary>The Maker's output: one small before/after simplification, carried with the brief it answered.</summary>
internal sealed record ProposedChange(
	string FilePath,
	string Before,
	string After,
	string Rationale,
	string Test,
	MakerBrief Brief,
	string ParseError = "")
{
	public override string ToString() =>
		ParseError.Length > 0
			? $"ParseError: {ParseError}"
			: $"""
			File     : {FilePath}
			Before   : {Before}
			After    : {After}
			Rationale: {Rationale}
			Test     : {Test}
			""";
}

/// <summary>The Checker's independent decision and any feedback to send back to the Maker.</summary>
internal sealed record LoopReviewResult(ProposedChange Proposal, bool Approved, IReadOnlyList<string> Feedback);

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

/// <summary>The green-or-red result of running the repository's own build/test commands.</summary>
internal sealed record BuildOracleResult(bool Passed, string Summary);

/// <summary>
/// The cheap, hard-to-fake oracle the loop trusts: run the repository's own build (and tests when a
/// test project exists) exactly as a CI gate would. No model call — ordinary deterministic code.
/// </summary>
internal static class BuildOracle
{
	private const int TimeoutSeconds = 120;

	public static BuildOracleResult Run(string repoRoot)
	{
		var build = Run("dotnet", "build --nologo -v minimal", repoRoot);

		var hasTestProject = Directory
			.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
			.Any(project => project.Contains(".Tests", StringComparison.OrdinalIgnoreCase));

		if (!hasTestProject)
		{
			return new BuildOracleResult(
				build.Passed,
				$"dotnet build {(build.Passed ? "passed" : "failed")} in {Path.GetFileName(repoRoot)}");
		}

		var test = Run("dotnet", "test --nologo --no-build -v minimal", repoRoot);
		var passed = build.Passed && test.Passed;
		var summary = $"build {(build.Passed ? "passed" : "failed")}; tests {(test.Passed ? "passed" : "failed")} in {Path.GetFileName(repoRoot)}";
		return new BuildOracleResult(passed, summary);
	}

	private static (bool Passed, string Tail) Run(string fileName, string arguments, string workingDirectory)
	{
		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				WorkingDirectory = workingDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};

			using var process = new Process { StartInfo = startInfo };
			process.Start();

			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();

			if (!process.WaitForExit(TimeSpan.FromSeconds(TimeoutSeconds)))
			{
				process.Kill(entireProcessTree: true);
				return (false, "oracle timed out");
			}

			var output = stdout.Result + stderr.Result;
			var tail = string.Join(
				Environment.NewLine,
				output.ReplaceLineEndings().Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0).TakeLast(4));
			return (process.ExitCode == 0, string.IsNullOrWhiteSpace(tail) ? "no output" : tail);
		}
		catch (Exception ex)
		{
			return (false, $"oracle could not run ({ex.GetType().Name}: {ex.Message})");
		}
	}
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

		return text.Length <= maxLength ? text : $"{text[..maxLength]}\n… (excerpt truncated)";
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

/// <summary>Finds the directory that owns a project file by walking up from the target file.</summary>
internal static class ProjectRoot
{
	public static string Find(string filePath)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))
			?? Directory.GetCurrentDirectory();

		var current = new DirectoryInfo(directory);
		while (current is not null)
		{
			if (Directory.EnumerateFiles(current.FullName, "*.csproj").Any())
			{
				return current.FullName;
			}

			current = current.Parent;
		}

		return directory;
	}
}

