# Implementation Plan: StardewAI

## Overview

Turn the existing mod skeleton into a working v1: a local-LLM-powered, native-chat-box-driven cheat dispatcher. Work proceeds bottom-up — config and state digest, then the provider-agnostic bridge, then the thread-safe execution pipeline, then the chat hook, then resilience and packaging, then verification.

## Task Dependency Graph

```json
{
  "waves": [
    { "wave": 1, "tasks": ["1", "2"], "dependsOn": [] },
    { "wave": 2, "tasks": ["3"], "dependsOn": ["1", "2"] },
    { "wave": 3, "tasks": ["4"], "dependsOn": ["3"] },
    { "wave": 4, "tasks": ["5"], "dependsOn": ["4"] },
    { "wave": 5, "tasks": ["6"], "dependsOn": ["5"] },
    { "wave": 6, "tasks": ["7"], "dependsOn": ["6"] },
    { "wave": 7, "tasks": ["8"], "dependsOn": [] },
    { "wave": 8, "tasks": ["9"], "dependsOn": ["7", "8"] }
  ]
}
```

- Tasks 1 and 2 are independent and can run in parallel.
- Task 3 depends on 1 (config) and 2 (digest shape).
- Tasks 4→5→6→7 are sequential.
- Task 8 (packaging) can proceed anytime; required before release.
- Task 9 (verification) is last.

## Tasks

## 1. Project & config
- [ ] 1.1 Fix `config.json` model: add `BaseUrl`, `Model`, `ApiKey`, `Hotkey`, `Temperature`, `MaxTokens`, `HistoryTurns`, `TimeoutSeconds` to `ModConfig`. Remove the Anthropic-only key.
- [ ] 1.2 Add `config.example.json` and git-ignore real `config.json`.
- [ ] 1.3 Confirm `.csproj` targets the installed SMAPI/Stardew 1.6 assemblies; verify build references resolve.
- _Requirements: R1, R8_

## 2. State digest
- [ ] 2.1 Replace verbose state with `StateDigest`: compact object + human-readable string.
- [ ] 2.2 Summarize crops (counts: total/occupied/needWater/ready, grouped by name) instead of per-tile dumps.
- [ ] 2.3 Cap inventory to top ~15 meaningful items.
- _Requirements: R3_

## 3. Provider-agnostic AIBridge
- [ ] 3.1 Rewrite `AIBridge` to POST OpenAI-compatible `/chat/completions` at configurable `BaseUrl`.
- [ ] 3.2 Default to Ollama (`http://localhost:11434/v1`), no auth; add Bearer only when `ApiKey` set.
- [ ] 3.3 Add structured-output constraint (Ollama `format` schema; `response_format json_schema` for compatible endpoints).
- [ ] 3.4 Add 30s timeout, low temperature, history messages.
- [ ] 3.5 Remove hardcoded/fake model id; read from config.
- _Requirements: R1, R2, R4_

## 4. Thread-safe action pipeline
- [ ] 4.1 Add `ActionQueue` (`ConcurrentQueue`) for AI responses.
- [ ] 4.2 Background task enqueues result; never mutates `Game1` directly.
- [ ] 4.3 Drain queue in `GameLoop.UpdateTicked` on the main thread.
- [ ] 4.4 Call `ActionExecutor.Init(Monitor)` in `Entry` (fix null Monitor).
- _Requirements: R5_

## 5. ActionExecutor — validate + execute
- [ ] 5.1 Wire `ActionExecutor.Execute` into the drain path (currently never called).
- [ ] 5.2 Validate every action before mutating; skip invalid with warning, continue batch.
- [ ] 5.3 Fix 1.6 APIs: `setSeason` via `Game1.season` enum + `setGraphicsForSeason()`; `setWeather` via `netWorldState`/location weather, not legacy bool-only.
- [ ] 5.4 Implement new actions: `setTime` (clamped), `setDay`, `warp` (resolve location), `setSkillXp`, `setFriendship`, `waterAllCrops`.
- [ ] 5.5 Clamp `addMoney` ≥ 0; resolve `addItem` via `ItemRegistry.Create`.
- _Requirements: R5, R6_

## 6. Chat box hook + history
- [ ] 6.1 Hook Stardew's native chat box (T / `/`) as primary input; intercept submitted messages via SMAPI. No custom menu for core use.
- [ ] 6.2 Route submitted text to the LLM only when world is loaded; otherwise behave as normal chat. Optional F8 fallback triggers the same flow.
- [ ] 6.3 On submit: build digest (main thread), append to history, fire async request, print "thinking" then the AI `message` back into the chat box.
- [ ] 6.4 Implement `ConversationHistory` ring buffer (default 6 turns); log `reasoning`.
- _Requirements: R4_

## 7. Resilience
- [ ] 7.1 Unreachable endpoint / timeout → HUD error, keep running.
- [ ] 7.2 Invalid JSON → HUD "couldn't understand", log raw, no mutation.
- _Requirements: R2, R7_

## 8. Open-source packaging
- [ ] 8.1 README: Ollama setup, `ollama pull` recommended models, build + install steps, low-VRAM model list.
- [ ] 8.2 Add LICENSE.
- [ ] 8.3 Verify no secrets committed; example config only.
- _Requirements: R8_

## 9. Verification
- [ ] 9.1 Build the mod; confirm it loads in SMAPI without errors.
- [ ] 9.2 Manual: "give me 10k and make it rain" → money + weather change, no crash.
- [ ] 9.3 Manual: unreachable endpoint → graceful HUD error.
- [ ] 9.4 Manual: malformed model output (force it) → no crash, no mutation.
- [ ] 9.5 Confirm runs against a local 3B–7B model end-to-end.

## Phase 2 (DONE)
- [x] Player 2 elf laborer: `assignTask` action + deterministic `TaskRunner` (Junimo-style, NO LLM in the loop), simple helper sprite, bounded-area water/harvest/refill. _Requirements: R9_ — `TaskRunner.cs`; helper is a green Junimo `TemporaryAnimatedSprite` that walks the field tick-by-tick on `UpdateTicked`. Chores: water (set `HoeDirt` watered), harvest (`Crop.harvest`), refill (top up watering can). Supports a bounded `{x,y,w,h}` area or the whole location, plus `repeat`. `cancelTask` removes it.
- [~] Real walking farmhand sprite + pathfinding (showpiece version of the elf). — Helper sprite walks straight-line toward each target tile (no obstacle pathfinding yet); full A*/farmhand sprite remains a later showpiece.
- [x] Expose action set as an MCP server (plug into Companions/agent ecosystem, still local via Ollama-MCP). — `McpServer.cs`: HTTP + JSON-RPC 2.0 on `localhost:McpPort`. Implements `initialize`, `tools/list`, `tools/call`. Tools mirror `ActionTools.Schema`; calls are normalized to the executor's actions JSON and run on the main thread. Off by default (`EnableMcpServer`).
- [x] Function-calling API style instead of single JSON blob. — `ActionTools.cs` defines the OpenAI `tools` schema; `AIBridge` adds the `tools` array when `UseFunctionCalling` is set and converts `tool_calls` back into actions.
- [x] Streaming responses into the chat box. — `AIBridge.AskStream` reads SSE deltas; `ModEntry.RenderStreaming` shows the live message in a HUD line as it types; final message is also printed into the native chat box.
- [x] OBS stream-overlay panel (digest + last action). — `OverlayServer.cs`: serves a browser-source HTML panel at `http://localhost:OverlayPort/` that polls `/data` for the live digest, last action, and elf-active flag.

## Notes

- Priority is fixed: chat-box hook (the premise) → instant cheat dispatch (the payload) → elf laborer (optional flex, phase 2). Do not invert.
- The model is never in the execution loop. It fires once per message to produce a validated action set; all labor is deterministic C#.
- Keep v1 direct OpenAI-compatible (Ollama). MCP exposure is a phase-2 bridge, not a v1 dependency.
- Every numbered task maps to requirements in `requirements.md`; design rationale lives in `design.md`.
