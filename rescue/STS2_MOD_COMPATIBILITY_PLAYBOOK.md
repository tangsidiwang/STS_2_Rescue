# STS2 Mod Compatibility Playbook (from rescue)

## Scope
This document summarizes compatibility lessons from this conversation and turns them into reusable practices for other Slay the Spire 2 mods.

Core constraints:
- Do not modify vanilla game code.
- Only adapt via mod code + Harmony patches.
- Assume official updates can change method signatures, constructors, and internal flow.

## Real Issues Encountered

### 1) Compile-time break from API signature change
Symptom:
- Build failed with CS7036 around `Hook.BeforePlayPhaseStart(...)`.

Root cause:
- Official update changed the expected call shape/signature.
- Direct static call in mod code became invalid.

Fix pattern:
- Do not hard-bind a single call signature in gameplay-critical path.
- Introduce a compatibility wrapper:
  - Try old known signature first.
  - Fallback to reflective path for newer signature/flow.

---

### 2) Runtime freeze-like behavior after revive card
Symptom:
- Card appeared stuck after reviving teammate corpse.
- Log showed repeated action exceptions.

Key log signal:
- `MissingMethodException: Constructor on type HookPlayerChoiceContext not found`.

Root cause:
- Constructor shape changed across versions.
- Mod attempted to instantiate using old constructor arguments.
- Exception bubbled through action execution chain, making card behavior look frozen.

Fix pattern:
- Constructor compatibility must be resilient and non-throwing in fallback path.
- Build constructor arguments by parameter type, not by a fixed positional assumption.
- For enum parameters, prefer named value (e.g. `Combat`) if available, otherwise fallback to first enum value.
- If no compatible constructor is found, fail gracefully and skip that hook branch instead of crashing action flow.

---

### 3) Mod failed to load due to manifest dependency config
Symptom:
- Loader error at startup: depends on mods not loaded.

Root cause:
- Manifest contained `"dependencies": [""]` (empty-string dependency).

Fix:
- Use an actual empty array for no dependencies:
  - `"dependencies": []`

## Reusable Compatibility Design Rules

### Rule A: Separate compatibility layer from gameplay logic
- Keep version-adaptation logic inside dedicated compat helpers/wrappers.
- Keep gameplay behavior code clean and version-agnostic.

### Rule B: Prefer "probe then execute" over hard assumptions
- Probe methods/constructors via reflection.
- Execute only when probe succeeds.
- Never let compat probing throw into main gameplay flow.

### Rule C: Prefer type-based constructor argument mapping
When creating framework objects reflectively:
- Match by parameter type (`AbstractModel`, `CombatState`, `Player`, `ulong`, enum).
- Respect default parameters where available.
- Avoid relying on constructor parameter count/order only.

### Rule D: Keep graceful degradation
- If a non-critical hook cannot be called due to version mismatch, skip that branch.
- Preserve card/action completion whenever possible.

### Rule E: Startup config is part of compatibility
- Validate mod manifest fields as part of update migration.
- Invalid manifest can look like code failure but happens before code executes.

## Fast Triage Workflow for Future Updates

1. Build first
- Run project build and fix compile errors caused by signature/type changes.

2. Reproduce once in-game
- Trigger the exact behavior path (e.g., revive card flow).

3. Read log by exception-first strategy
- Search for: `ERROR`, `Exception`, `MissingMethodException`, `MissingFieldException`, `HarmonyException`.
- Trace first actionable mod frame (your file/function), not only engine frames.

4. Patch with compat wrapper
- Avoid one-off direct replacement.
- Add probe + fallback + graceful skip.

5. Rebuild and retest the exact scenario
- Confirm both compile success and runtime action completion.

## High-Value Log Patterns

- `MissingMethodException`:
  - Usually method/constructor signature drift after update.

- `MissingFieldException` / `MissingMemberException`:
  - Internal/private member renamed or removed.

- `HarmonyException` during patching:
  - Patch target changed name/signature; update target resolution.

- Loader dependency errors:
  - Manifest/config issue, not gameplay code issue.

## Practical Checklist Before Releasing Cross-Version Update

- [ ] No direct fragile calls in critical action path without fallback.
- [ ] Constructor creation paths are reflection-safe and non-throwing.
- [ ] Manifest dependencies are valid (`[]` if none).
- [ ] Build passes with target game DLL.
- [ ] Core scenario test passes (the exact previously broken flow).
- [ ] Log is clean of repeat exceptions in that scenario.

## Suggested Minimal Compat Utility Pattern

Use a helper pattern similar to:
- `TryInvokeKnownMethod(...)` -> success/false
- `TryCreateContextByTypeMapping(...)` -> context/null
- `TryInvokeNewFlow(...)` -> success/false
- Caller uses ordered fallback and never throws from compatibility probing.

## Notes specific to this rescue case

- Revive flow touched turn-start related hook sequencing.
- Action chain stability is more important than perfect hook coverage in mismatch cases.
- A skipped optional hook is preferable to breaking `PlayCardAction` completion.

---

If you copy this playbook into another mod, prioritize:
- one compat entry point per fragile system,
- exception-safe reflection,
- and log-driven verification for the exact broken gameplay path.
