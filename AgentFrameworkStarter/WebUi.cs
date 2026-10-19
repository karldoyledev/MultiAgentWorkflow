using AgentFrameworkStarter.Components;
using AgentFrameworkStarter.Services;

/// <summary>
/// Hosts the dark-themed Blazor Server UI that wraps the loop-engineering workflow inputs.
/// The top-right Demo/Live switch selects between the credential-free smoke test and the
/// full model run; the same switches the inputs the page shows and what WorkflowRunner executes.
/// </summary>
internal static class WebUi
{
	public static async Task StartAsync(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		// Default to a stable local URL; override with ASPNETCORE_URLS if you prefer.
		builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5080");

		builder.Services.AddRazorComponents().AddInteractiveServerComponents();
		builder.Services.AddScoped<WorkflowUiState>();
		builder.Services.AddScoped<WorkflowRunner>();

		var app = builder.Build();

		if (!app.Environment.IsDevelopment())
		{
			app.UseExceptionHandler("/Error", createScopeForErrors: true);
		}

		app.UseStaticFiles();
		app.UseAntiforgery();
		app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

		Console.WriteLine();
		Console.WriteLine("AgentFrameworkStarter UI  •  " + string.Join("  •  ", app.Urls));
		Console.WriteLine("Demo mode : credential-free loop smoke test");
		Console.WriteLine("Live mode : full model workflow (API key / model / base URL inputs)");
		Console.WriteLine("Press Ctrl+C to stop.");
		await app.RunAsync();
	}
}
