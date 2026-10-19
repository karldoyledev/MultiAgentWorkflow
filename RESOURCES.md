# Microsoft Agent Framework Resources

## Knowledge

- [Microsoft Learn: Agent Framework workflow orchestrations](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/)
  Official overview of sequential, concurrent, handoff, group chat, and Magentic patterns. Use when selecting a coordination shape.
- [Microsoft Learn: Workflow concepts](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/)
  Defines the .NET graph model, typed executors, edges, conditions, events, and state. Use when translating a chosen pattern into a C# workflow.
- [Microsoft Learn: Workflow builder and execution](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/builder-and-execution)
  Documents `WorkflowBuilder`, workflow runs, events, validation, and execution behavior. Use while implementing explicit graph control flow.
- [Microsoft Learn: Agents in workflows](https://learn.microsoft.com/en-us/agent-framework/workflows/agents-in-workflows)
  Shows agents as workflow participants and how agent steps connect through a sequential graph. Use when adding the current chat agent to a workflow.
- [Microsoft Learn: Sequential orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/sequential)
  Covers fixed ordered pipelines, context flow, custom executors, and human review. Use for draft-review or transform-validate sequences.
- [Microsoft Learn: Concurrent orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/concurrent)
  Covers independent parallel agent work, aggregation, and custom aggregators. Use when several independent judgments should be compared or combined.
- [Microsoft Learn: Handoff orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff)
  Explains dynamic agent-to-agent control transfer and interactive routing. Use for user conversations that move between specialists.
- [Microsoft Learn: Group chat orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/group-chat)
  Explains manager-selected speakers and bounded collaborative refinement. Use when several agents need iterative turns over shared context.
- [Microsoft Learn: Magentic orchestration](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/magentic)
  Documents manager-led dynamic planning, agent selection, progress tracking, replanning, and limits. Use for open-ended tasks only after simpler control flow is inadequate.
- [Microsoft Learn: Agent evaluation](https://learn.microsoft.com/en-us/agent-framework/agents/evaluation)
  Describes local checks, custom evaluators, repeated runs, and per-agent workflow evaluation. Use to compare a workflow with the single-agent baseline.
- [Microsoft Learn: Your first agent](https://learn.microsoft.com/en-us/agent-framework/get-started/your-first-agent)
  A minimal C# agent example. Use as the starting point for the existing console project.

## Wisdom (Communities)

- [Microsoft Agent Framework GitHub repository](https://github.com/microsoft/agent-framework)
  Official samples and project discussions/issues. Use to check evolving API details and see implementation questions from practitioners.

## Gaps

- Provider-specific behavior for the company's OpenAI-compatible endpoint has not been assessed. Recheck supported chat-completion features when that endpoint is selected.