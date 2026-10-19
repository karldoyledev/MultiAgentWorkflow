using Microsoft.Agents.AI.Workflows;

/// <summary>
/// Step 5 of the Agent Framework "Get Started" series, kept deliberately small: two executors, one edge,
/// and no model calls, so the graph mechanics can be exercised without credentials or cost.
/// </summary>
internal static class Step5WorkflowBasics
{
	public static async Task RunAsync()
	{
		// An executor can be a plain function. BindAsExecutor adapts Func<TInput, TOutput> into a workflow node.
		Func<string, string> uppercaseFunc = text => text.ToUpperInvariant();
		var uppercase = uppercaseFunc.BindAsExecutor("UppercaseExecutor");

		// Or it can be a class deriving from Executor<TInput, TOutput>, which is easier to test and to give dependencies.
		var reverse = new ReverseTextExecutor();

		// The builder wires executors into a directed graph; the constructor argument is the starting executor.
		WorkflowBuilder builder = new(uppercase);

		// The edge carries the uppercase result into the reverse executor, which is registered as the workflow output.
		builder.AddEdge(uppercase, reverse).WithOutputFrom(reverse);
		var workflow = builder.Build();

		// Non-streaming execution runs to completion, then exposes everything that happened as events.
		await using Run run = await InProcessExecution.RunAsync(workflow, "Hello, World!");

		foreach (WorkflowEvent workflowEvent in run.NewEvents)
		{
			if (workflowEvent is ExecutorCompletedEvent completed)
			{
				Console.WriteLine($"{completed.ExecutorId}: {completed.Data}");
			}

			if (workflowEvent is WorkflowOutputEvent output)
			{
				Console.WriteLine($"Workflow output from {output.ExecutorId}: {output.Data}");
			}
		}
	}
}

/// <summary>A class-based executor: the same shape the Planner, Worker, and Validator will use.</summary>
internal sealed class ReverseTextExecutor() : Executor<string, string>("ReverseTextExecutor")
{
	public override ValueTask<string> HandleAsync(
		string message,
		IWorkflowContext context,
		CancellationToken cancellationToken = default)
		=> ValueTask.FromResult(string.Concat(message.Reverse()));
}
