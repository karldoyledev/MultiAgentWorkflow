namespace AgentFrameworkStarter.Services;

/// <summary>
/// Circuit-scoped state behind the Demo/Live switch in the top-right of the layout.
/// Flipping the switch notifies subscribers (the layout and the input page) so the UI re-renders.
/// </summary>
public sealed class WorkflowUiState
{
	public bool IsLive { get; private set; }

	public event Action? Changed;

	public void SetLive(bool live)
	{
		if (IsLive == live)
		{
			return;
		}

		IsLive = live;
		Changed?.Invoke();
	}
}
