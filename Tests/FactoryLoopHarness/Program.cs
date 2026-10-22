// The factory-loop harness, now its own project. Runs the repeatable scenarios against an isolated
// throwaway project. Exit code = number of failed scenarios (0 = all green), so it can gate CI.
//
// Rule: any change to AgentFrameworkStarter must keep this green — enforced by the CI workflow
// (.github/workflows/ci.yml), and locally by `./verify.ps1` or `dotnet run --project Tests/FactoryLoopHarness`.
return await FactoryLoopHarness.RunAsync();
