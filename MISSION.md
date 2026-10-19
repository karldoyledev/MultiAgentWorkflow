# Mission: Microsoft Agent Framework Multi-Agent Workflows

## Why
Build a working C# multi-agent workflow that produces measurably better results than a single model call, and learn how to choose an orchestration pattern from the task's control flow rather than adding agents by instinct.

## Success looks like
- Explain the roles of an agent, a workflow, and an executor in the Microsoft Agent Framework.
- Choose and justify an orchestration pattern for a concrete task.
- Build and run a bounded Planner -> Worker -> Validator workflow with deterministic checks and a limited repair path.
- Compare the workflow against the existing single-agent baseline on representative tasks using explicit quality and reliability checks.

## Constraints
- Use C# and the existing `AgentFrameworkStarter` project.
- Keep the model endpoint configurable; OpenRouter is the current provider and a company OpenAI-compatible endpoint may be used later.
- Prefer small runnable steps and keep credentials out of source control.

## Out of scope
- Deploying agents to Microsoft Foundry or Azure as a service.
- Adopting Magentic or group-chat orchestration before a simpler baseline demonstrates a need.
- Replacing the current provider integration just to follow a different tutorial's setup.