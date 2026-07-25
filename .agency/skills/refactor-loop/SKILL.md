---
description: Refactor code in a loop until the build is clean and all tests pass.
when_to_use: When you need to perform a refactoring that must reach a verifiable end state (zero build errors, all tests green) and should keep iterating until that bar is met.
allowed-tools:
  - enable_goalkeeper
  - disable_goalkeeper
  - subagent_tool
---
You are executing a refactoring task that must reach a verifiable end state. Follow these steps exactly.

## Step 1 — Arm the Goalkeeper first, before any other text

Your first action in this turn MUST be to call `enable_goalkeeper`. Call it before writing any narration, explanation, or plan — do not describe what you are about to do, just call the tool. If the task gives you an explicit goal condition, pass it verbatim. Otherwise use a transcript-demonstrable condition following this pattern:

```
condition: "`dotnet build` shows 0 errors AND `dotnet test` exits 0, with the full command output printed in the conversation"
```

Adapt the condition to the actual commands relevant to this project, but the condition MUST be satisfiable by text that appears in the conversation transcript — the Goalkeeper reads only the transcript.

## Step 2 — Plan

After arming the goalkeeper, decompose the objective into an ordered, numbered list of concrete steps and write it in the conversation. Each step must be individually completable and verifiable.

## Step 3 — Work the plan

Execute each step of your plan:

- Make the code change.
- **Run `dotnet build` and print the full output in the conversation.** Do not summarise — paste the raw output so the Goalkeeper can read it.
- **Run `dotnet test` and print the full output in the conversation.** Do not summarise — paste the raw output.
- If either command fails, fix the errors and re-run before moving to the next step.

## Step 4 — Surface proof every turn

After every turn, the Goalkeeper reads the conversation and checks whether the condition is met. For it to return `Done`, the transcript must contain:

- The actual `dotnet build` output showing `0 Error(s)`.
- The actual `dotnet test` output showing all tests passing (e.g. `Passed` status with zero failures).

Do not declare yourself done in text — run the commands and let the output speak.

## Step 5 — Disarm if the goal becomes inapplicable

If you determine mid-run that the original goal is no longer relevant (for example, the objective changed or you discover the refactoring is not needed), call `disable_goalkeeper` and explain why in the conversation.

## Delegation via subagent_tool

For large or repetitive steps, you may delegate to a subagent:

```
subagent_tool(prompt="<narrow, single task>", clientName="<optional>", model="<optional>")
```

Each subagent receives only the prompt you give it — include all necessary context. The subagent's output will appear in the conversation and can be read by the Goalkeeper.
