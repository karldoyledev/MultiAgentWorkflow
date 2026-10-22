namespace AgentFrameworkStarter.Services;

/// <summary>One selectable option in the UI: a stable value and a short human label.</summary>
public sealed record SelectOption(string Value, string Label);

/// <summary>
/// Selectable options for the loop's UI. The loop itself is generic and prompt-driven: the page
/// offers the retry budget, model, and base-URL suggestions, and the task prompt is free text.
/// </summary>
public static class LoopOptions
{
	/// <summary>The bounded repair budget a run is allowed before exiting.</summary>
	public static IReadOnlyList<int> RetryBudgets { get; } = [1, 2, 3, 4, 5];

	/// <summary>Suggested model IDs. The provider decides what actually works; pick one or type your own.</summary>
	public static IReadOnlyList<SelectOption> Models { get; } =
	[
		new("gpt-4o-mini", "gpt-4o-mini"),
		new("gpt-4o", "gpt-4o"),
		new("gpt-4.1-mini", "gpt-4.1-mini"),
		new("claude-3-5-sonnet", "claude-3-5-sonnet"),
		new("claude-3-5-haiku", "claude-3-5-haiku")
	];

	/// <summary>Suggested OpenAI-compatible base URLs.</summary>
	public static IReadOnlyList<SelectOption> BaseUrls { get; } =
	[
		new("https://model.ai.cubic3.com", "cubic3 (default)"),
		new("https://api.openai.com/v1", "OpenAI"),
		new("https://openrouter.ai/api/v1", "OpenRouter")
	];
}
