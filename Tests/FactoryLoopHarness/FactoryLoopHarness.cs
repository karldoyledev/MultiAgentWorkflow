using System.Text;
using AgentFrameworkStarter.Services;
using Microsoft.Agents.AI.Workflows;

/// <summary>
/// Repeatable, credential-free harness that proves the software-factory loop's deterministic
/// machinery against an isolated throwaway project (a single-file console app in %TEMP%). Each
/// scenario drives a real executor - or the full Maker -&gt; Checker -&gt; BuildOracle -&gt; Reporter
/// graph - with controlled inputs and asserts the expected output.
///
/// This is its own project (Tests/FactoryLoopHarness). Exit code = number of failed scenarios
/// (0 = all green), so it can gate CI; see CONTRIBUTING.md and .github/workflows/ci.yml - the rule
/// is that any change to AgentFrameworkStarter must keep this green.
/// </summary>
internal static class FactoryLoopHarness
{
	private const string TaskTemplate = "Remove all trailing whitespace from Program.cs and confirm the project still builds.";
	private const string CleanProgram = "using System;\nConsole.WriteLine(\"factory harness ok\");\n";

	private const string CsProj =
		"""
		<Project Sdk="Microsoft.NET.Sdk">
		  <PropertyGroup>
		    <OutputType>Exe</OutputType>
		    <TargetFramework>net10.0</TargetFramework>
		    <Nullable>enable</Nullable>
		    <ImplicitUsings>enable</ImplicitUsings>
		  </PropertyGroup>
		</Project>
		""";

	private sealed record Assertion(string Label, bool Passed, string Expected, string Actual);

	private static readonly List<(string Scenario, List<Assertion> Assertions)> Results = new();

	public static async Task<int> RunAsync()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "factory-harness-" + Guid.NewGuid().ToString("N"));
		var projDir = Path.Combine(tempRoot, "FactoryProj");
		var program = Path.Combine(projDir, "Program.cs");

		Directory.CreateDirectory(projDir);
		File.WriteAllText(Path.Combine(projDir, "FactoryProj.csproj"), CsProj);
		File.WriteAllText(program, CleanProgram);

		Console.WriteLine();
		Console.WriteLine("Factory loop harness - repeatable scenarios (credential-free)");
		Console.WriteLine("Isolated project : " + projDir);
		Console.WriteLine();

		try
		{
			// One offline restore up front so every later `--no-restore` oracle build has assets.
			var restore = await FactoryDevTools.RunCommandAsync("dotnet", "restore", projDir, timeoutSeconds: 300);
			if (restore.ExitCode != 0)
			{
				Console.WriteLine("[preflight] dotnet restore FAILED: " + FactoryLoopWorkflow.Truncate(restore.CapturedOutput, 1500));
				return 1;
			}

			// 1. Oracle baseline ----------------------------------------------------------
			await ScenarioAsync("Oracle accepts a clean project", () =>
			{
				var result = FactoryDevTools.RunDotNetBuildOracleAsync(projDir).GetAwaiter().GetResult();
				Assert(result.ExitCode == 0, "exit code", "0", result.ExitCode.ToString());
				AssertContains("Build succeeded", result.CapturedOutput);
			});

			// 2. Oracle catches a broken change ------------------------------------------
			await ScenarioAsync("Oracle rejects a change that breaks the build, then accepts the fix", () =>
			{
				File.WriteAllText(program, "class Broken {\n");
				var broken = FactoryDevTools.RunDotNetBuildOracleAsync(projDir).GetAwaiter().GetResult();
				Assert(broken.ExitCode != 0, "exit code on broken source", "non-zero", broken.ExitCode.ToString());
				AssertContains("error", broken.CapturedOutput);

				File.WriteAllText(program, CleanProgram);
				var fixedBuild = FactoryDevTools.RunDotNetBuildOracleAsync(projDir).GetAwaiter().GetResult();
				Assert(fixedBuild.ExitCode == 0, "exit code after fixing", "0", fixedBuild.ExitCode.ToString());
			});

			// 3. Scripted Maker fixes trailing whitespace --------------------------------
			await ScenarioAsync("ScriptedMaker removes trailing whitespace and reports the change", () =>
			{
				File.WriteAllText(program, "using System;   \nConsole.WriteLine(\"harness\");   \n");
				var brief = new FactoryBrief(TaskTemplate, program, 1, [], "(no prior state)", 2);
				var output = new ScriptedCleanerMakerExecutor(projDir)
					.HandleAsync(FactoryStep.ForAttempt(brief), null!, CancellationToken.None)
					.GetAwaiter().GetResult();

				Assert(output.Change is not null, "a change is produced", "non-null", output.Change is null ? "null" : "present");
				Assert(output.Change!.BeforeContent != output.Change.AfterContent, "content changed", "true", "true");
				var cleaned = output.Change.AfterContent ?? string.Empty;
				var hasTrailing = cleaned.Split('\n').Any(line => line.EndsWith(' ') || line.EndsWith('\t'));
				Assert(!hasTrailing, "no trailing whitespace remains", "false", hasTrailing.ToString());
				Assert(output.Change.Summary == "Removed trailing whitespace", "summary text", "Removed trailing whitespace", output.Change.Summary);
			});

			// 4. Scripted Maker no-op on a clean file -----------------------------------
			await ScenarioAsync("ScriptedMaker produces no change on a clean file", () =>
			{
				File.WriteAllText(program, CleanProgram);
				var brief = new FactoryBrief(TaskTemplate, program, 1, [], "(no prior state)", 2);
				var output = new ScriptedCleanerMakerExecutor(projDir)
					.HandleAsync(FactoryStep.ForAttempt(brief), null!, CancellationToken.None)
					.GetAwaiter().GetResult();
				Assert(output.Change is null, "no change", "null", output.Change is null ? "null" : "change");
			});

			// 5. Scripted Checker gates --------------------------------------------------
			await ScenarioAsync("ScriptedChecker approves small diffs and rejects large ones", () =>
			{
				var brief = new FactoryBrief(TaskTemplate, program, 1, [], "(no prior state)", 2);
				var checker = new ScriptedApproverChecker();

				var smallBefore = "line a\nline b\nline c\n";
				var smallAfter = "line a\nline c\n";
				var small = new FactoryChange(program, "small change", smallBefore, smallAfter);
				var smallReview = checker.HandleAsync(new FactoryOutput(small, brief), null!, CancellationToken.None).GetAwaiter().GetResult();
				Assert(smallReview.Approved, "small diff approved", "true", smallReview.Approved.ToString());

				var bigBefore = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"line {i}"));
				var bigAfter = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"line {i} changed"));
				var big = new FactoryChange(program, "large change", bigBefore, bigAfter);
				var bigReview = checker.HandleAsync(new FactoryOutput(big, brief), null!, CancellationToken.None).GetAwaiter().GetResult();
				Assert(!bigReview.Approved, "large diff rejected", "false", bigReview.Approved.ToString());
				AssertContains("larger than a few lines", string.Join(" ", bigReview.Feedback));
			});


			// 6. Full loop: fixable change ends in a passing report -----------------------
			await ScenarioAsync("Full loop: a fixable change reaches a passing report and memory file", async () =>
			{
				File.WriteAllText(program, "using System;   \nConsole.WriteLine(\"harness\");   \n");
				var stateFile = Path.Combine(Path.GetTempPath(), $"factory-harness-pass-{Guid.NewGuid():N}.md");
				var report = await RunFullLoopAsync(projDir, program, stateFile);

				AssertContains("=== Deliverable ===", report);
				AssertContains("Checker approved and dotnet build passed (exit 0) after 1 maker attempt(s).", report);
				AssertContains("=== Human step (outer loop) ===", report);
				Assert(File.Exists(stateFile), "memory state file exists", "true", File.Exists(stateFile).ToString());
				var state = File.ReadAllText(stateFile);
				AssertContains("=== Validation ===", state);
				AssertContains("Removed trailing whitespace", state);
				TryDeleteFile(stateFile);
			});

			// 7. Full loop: nothing to fix exits with the honest failure report ----------
			await ScenarioAsync("Full loop: nothing to fix exits with the honest failure report", async () =>
			{
				File.WriteAllText(program, CleanProgram);
				var stateFile = Path.Combine(Path.GetTempPath(), $"factory-harness-clean-{Guid.NewGuid():N}.md");
				var report = await RunFullLoopAsync(projDir, program, stateFile);

				AssertContains("Did not pass within 2 attempt(s)", report);
				AssertContains("The Maker produced no change this attempt.", report);
				Assert(File.Exists(stateFile), "memory state file exists", "true", File.Exists(stateFile).ToString());
				AssertContains("Did not pass within 2 attempt(s)", File.ReadAllText(stateFile));
				TryDeleteFile(stateFile);
			});

			// 8. Tool helpers ------------------------------------------------------------
			await ScenarioAsync("Tool helpers: command-line splitter, diff evidence, repo-write guard", () =>
			{
				var tokens = FactoryDevTools.SplitCommandLine("\"a b\" c d");
				Assert(string.Join(",", tokens) == "a b,c,d", "split tokens", "a b,c,d", string.Join(",", tokens));

				Assert(FactoryDevTools.GitDiffBetween("same", "same") == "(no textual change)", "equal snapshots", "(no textual change)", "same");
				var oneSide = FactoryDevTools.GitDiffBetween("x", "xy");
				Assert(oneSide.Contains("diff --git") || oneSide.Contains("unavailable"), "differing snapshots", "diff header or git note", oneSide);

				var refusal = FactoryDevTools.WriteFile(Path.Combine("..", "escape.txt"), "nope", projDir);
				Assert(refusal.Contains("refusing"), "write outside repo refused", "refusing", refusal);
			});
		}
		finally
		{
			PrintSummary(tempRoot);
			TryDeleteTree(tempRoot);
		}

		return Results.Sum(r => r.Assertions.Any(a => !a.Passed) ? 1 : 0);
	}

	/// <summary>Drives the real factory-loop graph on the isolated project with a scripted Maker/Checker.</summary>
	private static async Task<string> RunFullLoopAsync(string projDir, string targetFile, string stateFile)
	{
		var loopRequest = new FactoryRequest(
			Task: TaskTemplate,
			TargetFile: targetFile,
			StateFilePath: stateFile,
			MaxAttempts: 2);

		return await FactoryLoopWorkflow.RunGraphAsync(
			new FactorySelectorExecutor(),
			new ScriptedCleanerMakerExecutor(projDir),
			new ScriptedApproverChecker(),
			new FactoryBuildOracleValidator(projDir),
			new FactoryReporterExecutor(stateFile),
			loopRequest);
	}

	private static Task ScenarioAsync(string name, Func<Task> body) => ScenarioAsyncCore(name, body);

	private static Task ScenarioAsync(string name, Action body) =>
		ScenarioAsyncCore(name, () =>
		{
			body();
			return Task.CompletedTask;
		});

	private static async Task ScenarioAsyncCore(string name, Func<Task> body)
	{
		var assertions = new List<Assertion>();
		Results.Add((name, assertions));

		AssertionContext.Active = assertions;
		try
		{
			await body();
		}
		catch (Exception ex)
		{
			assertions.Add(new Assertion("scenario threw an exception", false, "no exception", ex.GetType().Name + ": " + ex.Message));
		}
		finally
		{
			AssertionContext.Active = null;
		}
	}

	private static void Assert(bool condition, string label, string expected, string actual)
	{
		var list = AssertionContext.Active ?? throw new InvalidOperationException("Assert outside a scenario.");
		list.Add(new Assertion(label, condition, expected, actual));
	}

	private static void AssertContains(string expected, string actual) =>
		Assert(actual.Contains(expected, StringComparison.OrdinalIgnoreCase), $"contains \"{expected}\"", expected, actual);

private static void PrintSummary(string tempRoot)
	{
		var failed = 0;
		var total = 0;
		foreach (var (name, assertions) in Results)
		{
			var scenarioFailed = assertions.Any(a => !a.Passed);
			if (scenarioFailed)
			{
				failed++;
			}

			total += assertions.Count;
			Console.WriteLine();
			Console.WriteLine($"SCENARIO: {name}  =>  {(scenarioFailed ? "FAIL" : "PASS")}");
			foreach (var a in assertions)
			{
				Console.WriteLine($"  [{(a.Passed ? "PASS" : "FAIL")}] {a.Label}");
				if (!a.Passed)
				{
					Console.WriteLine($"         expected : {a.Expected}");
					Console.WriteLine($"         actual   : {FactoryLoopWorkflow.Truncate(a.Actual, 500)}");
				}
			}
		}

		var reportPath = Path.Combine(Path.GetTempPath(), "factory-harness-report.md");
		var sb = new StringBuilder();
		sb.AppendLine("# Factory loop harness report");
		sb.AppendLine();
		sb.AppendLine($"- Run: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
		sb.AppendLine($"- Isolated project: `{tempRoot}` (deleted after the run)");
		sb.AppendLine($"- Scenarios: {Results.Count}; failed: {failed}");
		foreach (var (name, assertions) in Results)
		{
			sb.AppendLine();
			sb.AppendLine($"## {name} — {(assertions.Any(a => !a.Passed) ? "FAIL" : "PASS")}");
			foreach (var a in assertions)
			{
				sb.AppendLine($"- [{(a.Passed ? "x" : " ")}] {a.Label} (expected: {a.Expected})");
			}
		}
		File.WriteAllText(reportPath, sb.ToString());

		Console.WriteLine();
		Console.WriteLine($"Scenarios : {Results.Count}  |  assertions : {total}  |  failed scenarios : {failed}");
		Console.WriteLine($"Report     : {reportPath}");
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
			// best effort
		}
	}

	private static void TryDeleteTree(string root)
	{
		try
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
		catch
		{
			// best effort; temp dirs are harmless
		}
	}

	/// <summary>Threads the currently building scenario's assertion list through the static helpers.</summary>
	private static class AssertionContext
	{
		public static List<Assertion>? Active { get; set; }
	}
}
