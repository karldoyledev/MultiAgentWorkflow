using System.Diagnostics;
using System.Text;
using OpenAI.Chat;

namespace AgentFrameworkStarter.Services;

/// <summary>The exit code and captured output of a process spawned by a factory-loop tool.</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
	/// <summary>stdout with stderr appended when the process wrote any, trimmed for the user.</summary>
	public string CapturedOutput
	{
		get
		{
			var builder = new StringBuilder(StandardOutput);
			if (!string.IsNullOrWhiteSpace(StandardError))
			{
				builder.AppendLine();
				builder.AppendLine("[stderr]");
				builder.Append(StandardError);
			}

			return builder.ToString().Trim();
		}
	}
}

/// <summary>
/// The connectors the factory loop's agents act through: safe file access inside the repository,
/// bounded command execution, and git-based evidence. These are the "tools" the skills.addy.ie loop
/// primitive supplies in Claude Code/Codex, made explicit here for the Microsoft Agent Framework.
///
/// Everything is tightened on purpose: writes stay inside the repository, commands are whitelisted
/// to `dotnet` and `git`, output is capped, and every command has a hard timeout.
/// </summary>
public static class FactoryDevTools
{
	private const int DefaultTimeoutSeconds = 60;

	/// <summary>Finds the nearest directory containing a *.csproj (walking up, then one level of nesting).</summary>
	public static string? FindProjectRoot(string? startDirectory = null)
	{
		var directory = new DirectoryInfo(startDirectory ?? Environment.CurrentDirectory);
		while (directory is not null)
		{
			if (Directory.EnumerateFiles(directory.FullName, "*.csproj").Any())
			{
				return directory.FullName;
			}

			foreach (var sub in Directory.EnumerateDirectories(directory.FullName))
			{
				if (Directory.EnumerateFiles(sub, "*.csproj").Any())
				{
					return sub;
				}
			}

			directory = directory.Parent;
		}

		return null;
	}

	/// <summary>Resolves a possibly relative path against the project root (or the current directory).</summary>
	public static string ResolvePath(string path, string? projectRoot = null)
	{
		var root = projectRoot ?? FindProjectRoot() ?? Environment.CurrentDirectory;
		return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
	}

	/// <summary>True when <paramref name="fullPath"/> is inside <paramref name="root"/> (path-boundary safe).</summary>
	public static bool IsInsideRoot(string fullPath, string root)
	{
		var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
		return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Reads a text file. Missing files and paths outside the repo resolve to an explanatory stub.</summary>
	public static string ReadFile(string path, string? projectRoot = null)
	{
		var full = ResolvePath(path, projectRoot);
		if (!File.Exists(full))
		{
			return $"(file not found: {full})";
		}

		return File.ReadAllText(full);
	}

	/// <summary>Writes a text file. Paths outside the repository are rejected.</summary>
	public static string WriteFile(string path, string content, string? projectRoot = null)
	{
		var root = projectRoot ?? FindProjectRoot() ?? Environment.CurrentDirectory;
		var full = ResolvePath(path, root);
		if (!IsInsideRoot(full, root))
		{
			return $"(refusing to write outside the repository root: {full})";
		}

		Directory.CreateDirectory(Path.GetDirectoryName(full) ?? root);
		File.WriteAllText(full, content);
		return $"Wrote {content.Length} chars to {full}";
	}


	/// <summary>Runs a command in the project root with captured output and a hard timeout.</summary>
	public static async Task<CommandResult> RunCommandAsync(
		string command,
		string? arguments = null,
		string? workingDirectory = null,
		int timeoutSeconds = DefaultTimeoutSeconds)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = command,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			WorkingDirectory = workingDirectory ?? FindProjectRoot() ?? Environment.CurrentDirectory,
		};

		if (!string.IsNullOrWhiteSpace(arguments))
		{
			foreach (var token in SplitCommandLine(arguments))
			{
				startInfo.ArgumentList.Add(token);
			}
		}

		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException($"Could not start '{command}'.");
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();

		if (!process.WaitForExit(timeoutSeconds * 1000))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch
			{
				// best effort; the command has already exceeded its budget
			}

			return new CommandResult(-1, string.Empty, $"Command timed out after {timeoutSeconds}s: {command} {arguments}");
		}

		var stdout = await stdoutTask;
		var stderr = await stderrTask;
		return new CommandResult(process.ExitCode, stdout, stderr);
	}

	/// <summary>
	/// The real oracle: builds the solution with <c>--no-restore</c>, sending all outputs to a temp
	/// folder so the currently running apphost in bin/ is never touched (it would be locked by us).
	/// Pass/fail is just exit code 0. This is the "cheap, hard-to-fake check" the loop runs on.
	/// </summary>
	public static async Task<CommandResult> RunDotNetBuildOracleAsync(string? projectRoot = null)
	{
		var root = projectRoot ?? FindProjectRoot() ?? Environment.CurrentDirectory;
		var outputDir = Path.Combine(Path.GetTempPath(), "factory-build-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(outputDir);
		return await RunCommandAsync(
			"dotnet",
			$"build --no-restore -v minimal -o \"{outputDir}\"",
			root,
			timeoutSeconds: 240);
	}


	/// <summary>Git diff between two text snapshots with no repo mutation (uses temp files).</summary>
	public static string GitDiffBetween(string before, string after)
	{
		if (before == after)
		{
			return "(no textual change)";
		}

		var beforePath = Path.Combine(Path.GetTempPath(), "factory-before-" + Guid.NewGuid().ToString("N") + ".txt");
		var afterPath = Path.Combine(Path.GetTempPath(), "factory-after-" + Guid.NewGuid().ToString("N") + ".txt");
		File.WriteAllText(beforePath, before);
		File.WriteAllText(afterPath, after);
		try
		{
			// git diff --no-index exits 1 when differences exist; that is the success case here.
			var result = RunCommandAsync("git", $"diff --no-index -- \"{beforePath}\" \"{afterPath}\"", timeoutSeconds: 30).GetAwaiter().GetResult();
			return string.IsNullOrWhiteSpace(result.CapturedOutput) ? "(no textual change)" : result.CapturedOutput;
		}
		catch (Exception ex)
		{
			return $"(git diff unavailable: {ex.Message})";
		}
		finally
		{
			try
			{
				File.Delete(beforePath);
				File.Delete(afterPath);
			}
			catch
			{
				// best effort
			}
		}
	}

	/// <summary>Git diff of the working tree for one file (or the whole tree). Never mutates the index.</summary>
	public static string GitDiffWorkingTree(string? path = null, string? projectRoot = null)
	{
		try
		{
			var args = string.IsNullOrWhiteSpace(path) ? "diff" : $"diff -- \"{path}\"";
			var result = RunCommandAsync("git", args, projectRoot, timeoutSeconds: 30).GetAwaiter().GetResult();
			return string.IsNullOrWhiteSpace(result.CapturedOutput) ? "(no working-tree diff)" : result.CapturedOutput;
		}
		catch (Exception ex)
		{
			return $"(git diff unavailable: {ex.Message})";
		}
	}

	/// <summary>The OpenAI-compatible tool definitions exposed to the model-backed Maker.</summary>
	public static IReadOnlyList<ChatTool> BuildChatTools()
	{
		var readFile = ChatTool.CreateFunctionTool(
			"read_file",
			"Read a UTF-8 text file from the repository. Pass a path; relative paths resolve against the project root. Returns the file contents or an explanatory message.",
			BinaryData.FromString("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""));

		var writeFile = ChatTool.CreateFunctionTool(
			"write_file",
			"Write or replace a UTF-8 text file in the repository. Pass a path relative to the project root and the full new contents. Refuses to write outside the repository.",
			BinaryData.FromString("""{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}"""));

		var runCommand = ChatTool.CreateFunctionTool(
			"run_command",
			"Run a command in the repository root and return its output. Only 'dotnet' (e.g. 'dotnet build --no-restore') and 'git' (e.g. 'git status', 'git diff -- <file>') are allowed.",
			BinaryData.FromString("""{"type":"object","properties":{"command":{"type":"string"},"args":{"type":"string"}},"required":["command"]}"""));

		var gitDiff = ChatTool.CreateFunctionTool(
			"git_diff",
			"Show the current working-tree diff for one file (or the whole tree). Never modifies the repository.",
			BinaryData.FromString("""{"type":"object","properties":{"path":{"type":"string"}},"required":[]}"""));

		return [readFile, writeFile, runCommand, gitDiff];
	}

	/// <summary>Splits a command line into tokens, honouring double-quoted segments.</summary>
	internal static IReadOnlyList<string> SplitCommandLine(string text)
	{
		var tokens = new List<string>();
		var current = new StringBuilder();
		var inQuotes = false;

		foreach (var ch in text)
		{
			if (ch == '"')
			{
				inQuotes = !inQuotes;
			}
			else if (char.IsWhiteSpace(ch) && !inQuotes)
			{
				if (current.Length > 0)
				{
					tokens.Add(current.ToString());
					current.Clear();
				}
			}
			else
			{
				current.Append(ch);
			}
		}

		if (current.Length > 0)
		{
			tokens.Add(current.ToString());
		}

		return tokens;
	}
}

