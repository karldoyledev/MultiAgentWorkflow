# Gate: builds the solution and runs the factory-loop harness.
# Rule (see CONTRIBUTING.md): any change must keep this exit code 0.
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot

Write-Host "==> build solution"
dotnet build (Join-Path $root "MultiAgentWorkflow.slnx") -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> run factory-loop harness (Tests/FactoryLoopHarness)"
dotnet run --project (Join-Path $root "Tests\FactoryLoopHarness\FactoryLoopHarnessTests.csproj")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> credential-free loop smoke test"
dotnet run --project (Join-Path $root "AgentFrameworkStarter\AgentFrameworkStarter.csproj") -- loop-demo
exit $LASTEXITCODE
