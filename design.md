# StardewAI — Design

## Overview

StardewAI hooks Stardew's native chat box and routes typed messages to a local LLM that acts as a one-shot tool-dispatcher into a bounded, validated action set. The model proposes; C# validates and executes on the main game thread. This section covers architecture, components, and data models; correctness properties and testing close it out.

## Architecture

```
            ┌─────────────────────────────────────────────────────────┐
            │                     Stardew Valley                        │
            │                    (SMAPI runtime)                        │
            │                                                           │
  F8 ──────▶│  ChatUI ──text──▶ ModEntry ──▶ StateDigest.Build()        │
            │                       │                │                  │
            │                       ▼                ▼                  │
            │                  ConversationHistory   (compact JSON)     │
            │                       │                                   │
            │                       ▼                                   │
            │                   AIBridge.AskAsync() ──── off-thread ────┼──▶ Local LLM
            │                       │                                   │   (Ollama /v1,
            │                       ▼ (JSON result)                     │    LM Studio,
            │                  ActionQueue.Enqueue()                    │    or cloud)
            │                       │                                   │
            │   UpdateTicked ──▶ ActionQueue.Drain() ──▶ ActionExecutor │
            │                       (MAIN THREAD ONLY)     │            │
            │                                              ▼            │
            │                                      validate → mutate Game1
            └─────────────────────────────────────────────────────────┘
```

Key principle: **the LLM proposes, C# validates and disposes.** The model is a cheap router; the mod owns all correctness and all state mutation happens on the main thread.

## Data Models

### GameState digest (sent to model)
Compact, human-readable summary — not raw dumps:
- `World`: season, day, year, time, weather.
- `Player`: name, money, energy, maxEnergy.
- `Farm`: total/occupied plots, crops grouped by name with counts (needWater, readyToHarvest).
- `Inventory`: top ~15 items (name, qty, quality).

### Action (model output)
Typed objects per the response contract above. Each has a `type` discriminator plus type-specific fields. Validated in C# before execution.

### Config (`config.json`)
See the Config component below for the full shape.

## Components and Interfaces

### ModEntry
- Reads config, constructs `AIBridge`, calls `ActionExecutor.Init(Monitor)`.
- Registers `Input.ButtonPressed` (open chat) and `GameLoop.UpdateTicked` (drain queue).
- Owns the `ConversationHistory` ring buffer and the `ActionQueue`.

### StateDigest (replaces verbose GameStateReader output)
- Produces a **compact** digest object + a human-readable string.
- Fields: season, day, year, time, weather, money, energy/maxEnergy, crop summary (total/occupied/needWater/readyToHarvest, grouped by crop name), top inventory items (capped, e.g. 15).
- Rationale: small models degrade with large noisy context. Small digest = higher accuracy + lower latency. Doubles as stream overlay text.

### AIBridge (provider-agnostic)
- POSTs to `{BaseUrl}/chat/completions` (OpenAI-compatible). Default `http://localhost:11434/v1`.
- Sends `model`, `messages` (system + history + current), `max_tokens`, `temperature` (low, ~0.2), and **structured-output constraint**:
  - Ollama: `format` = JSON schema (or `"json"`).
  - OpenAI-compatible cloud / LM Studio: `response_format: { type: "json_schema", json_schema: {...} }`.
- Optional `Authorization: Bearer` only if key present.
- 30s timeout. Returns raw JSON string or null on failure.
- Runs via `Task.Run`; result is handed back through the `ActionQueue`, **never** mutating game state directly.

### Response contract (JSON schema)
```json
{
  "reasoning": "string",
  "message": "string",
  "actions": [
    { "type": "addItem",    "itemId": "(string)", "quantity": 1 },
    { "type": "removeItem",  "name": "(string)",   "quantity": 1 },
    { "type": "addMoney",    "amount": 0 },
    { "type": "setWeather",  "weather": "sunny|rain|storm|snow" },
    { "type": "setSeason",   "season": "spring|summer|fall|winter" },
    { "type": "setTime",     "time": 600 },
    { "type": "setDay",      "day": 1 },
    { "type": "warp",        "location": "Farm" },
    { "type": "setSkillXp",  "skill": "farming|mining|foraging|fishing|combat", "xp": 0 },
    { "type": "setFriendship","npc": "(string)", "points": 0 },
    { "type": "waterAllCrops" },
    { "type": "clearCrops" },
    { "type": "message" }
  ]
}
```
The schema is enforced at the endpoint where supported, and re-validated in C#.

### ActionQueue (thread bridge)
- `ConcurrentQueue<string>` of AI JSON responses (or pre-parsed action lists).
- `Enqueue` called from the background task; `Drain` called from `UpdateTicked` on the main thread.
- This is the fix for the #1 crash risk: Stardew/MonoGame are not thread-safe.

### ActionExecutor (validation + mutation)
- `Init(IMonitor)` must be called once (fixes null Monitor).
- `Execute(json)`: parse → show `message` HUD → log `reasoning` → iterate `actions`.
- Per-action **validate then mutate**:
  - `addItem`: `ItemRegistry.Create(itemId, qty)`; skip if null.
  - `addMoney`: clamp result ≥ 0.
  - `setWeather` (1.6): set `Game1.netWorldState.Value` weather for the location / `Game1.weatherForTomorrow` pattern; use `LocationWeather`. Avoid legacy `Game1.isRaining`-only writes (they don't persist/render reliably in 1.6).
  - `setSeason` (1.6): `Game1.season` enum (`Season.Spring`…), then `Game1.setGraphicsForSeason()`.
  - `setTime`: clamp to [600, 2600], align to valid 10-min increments.
  - `warp`: resolve `Game1.getLocationFromName`; skip if unknown.
  - `setSkillXp` / `setFriendship`: bounded writes via farmer API.
  - `waterAllCrops`: iterate `HoeDirt`, set watered state on main thread.
  - Each action wrapped in try/catch; one failure never aborts the batch.

### ChatUI — hook the native chat box (primary interface)
- **Do not build a custom menu for core use.** Hook Stardew's built-in `ChatBox` (opened with T / `/`). Intercept submitted messages via SMAPI (patch/observe the chat box, or read the chat input on submit).
- Route a submitted message to the LLM only when the world is loaded; otherwise let it behave as normal chat.
- On submit: push to history, build digest (main thread), fire `AIBridge.AskAsync`, show a "thinking" line in the chat box.
- Print the AI `message` back into the chat box so the conversation reads inline — the empty single-player chat box now talks back.
- A configurable fallback hotkey (default F8) can trigger the same flow, but it is secondary.
- Rationale: the native chat box IS the premise. Reusing it means zero custom UI for v1 and an instantly-readable demo (press the key everyone knows, type, it happens).

### Config (`config.json`, git-ignored)
```jsonc
{
  "BaseUrl": "http://localhost:11434/v1",
  "Model": "qwen2.5:7b-instruct",
  "ApiKey": "",                 // blank for local
  "Hotkey": "F8",
  "Temperature": 0.2,
  "MaxTokens": 1024,
  "HistoryTurns": 6,
  "TimeoutSeconds": 30
}
```

## Model guidance (docs)

Default local picks (low VRAM, good tool/JSON behavior):
- `qwen2.5:7b-instruct` (default) / `qwen2.5:3b-instruct` (leaner)
- `llama3.2:3b-instruct`
- NVIDIA Nemotron Nano class models (built for efficient agentic tool use)

Why local works here: Stardew is GPU-light, so a Q4 3B–7B model co-resides comfortably. Schema enforcement + C# validation make small models reliable for bounded dispatch.

## Error handling

| Failure | Behavior |
|---|---|
| Endpoint unreachable | HUD error, keep running |
| Timeout (30s) | HUD "model timed out", keep running |
| Invalid JSON | HUD "couldn't understand", log raw, no mutation |
| Invalid single action | skip + warn, continue batch |
| World not loaded | hotkey inert |

## Threading model

- LLM I/O: background `Task`.
- ALL `Game1` reads for the digest and ALL mutations: main thread (digest built at request time on main thread; mutations via `UpdateTicked` drain).

## Correctness Properties

### Property 1: No off-thread mutation
Every `Game1` write happens on the main thread (via the action queue drain). No exceptions.
**Validates: Requirements 5.1**

### Property 2: Validate-before-mutate
No action mutates state until its fields pass validation (id resolves, enum legal, bounds clamped).
**Validates: Requirements 5.2**

### Property 3: Fault isolation
One invalid action or a parse failure never aborts the batch or crashes the game.
**Validates: Requirements 5.3, 2.2**

### Property 4: No-trust parsing
The model output is always treated as untrusted; the schema + C# validation are the source of truth.
**Validates: Requirements 2.1, 2.2**

### Property 5: Bounded resource use
Request times out (default 30s); history is capped; digest is summarized — so cost stays flat regardless of save size.
**Validates: Requirements 7.2, 3.1**

### Property 6: Local-by-default, no secret required
With default config, a request succeeds against a local endpoint with no API key present.
**Validates: Requirements 1.2, 1.3**

## Testing Strategy

- **Unit (pure C#):** action validation (clamping, enum legality, unknown-location/skip), digest summarization, JSON parsing of well-formed and malformed model output.
- **Integration (SMAPI runtime):** load mod, open chat box, submit a message, confirm the action queue drains and state changes on the main thread without errors.
- **Manual/demo scenarios:** "give me 10k and make it rain" (multi-action), unreachable endpoint (graceful HUD error), forced malformed output (no crash, no mutation), end-to-end against a local 3B–7B model.
- **Regression guard:** confirm 1.6 weather/season APIs render and persist after a save/load cycle.

## Phase 2 (SHIPPED)

### Player 2 elf laborer (the "flex") — `TaskRunner.cs`
The key insight that makes this cheap: **no LLM in the execution loop.** The model fires once to turn "keep the east field watered and harvest when ready" into a structured `assignTask`. A deterministic C# **TaskRunner** then executes it tick-by-tick on `UpdateTicked` — exactly how vanilla Junimos work. So the model is idle during the actual labor; VRAM/latency stay trivial.

- `assignTask` action: `{ chore: water|harvest|refill, area: {x,y,w,h} | "farm", repeat: bool }`. `cancelTask` stops/removes the helper.
- `TaskRunner`: scans tiles in the area, queues the ones needing work, and each tick walks the helper toward the next target tile and performs the chore on arrival (set `HoeDirt` watered, `Crop.harvest` ready crops, refill the watering can). `repeat` re-scans after each pass. Runs only on the main thread.
- Visual: a green Junimo `TemporaryAnimatedSprite` that walks the field in-radius. Straight-line movement (no obstacle pathfinding) — the "real walking farmhand with A* pathfinding" remains the later showpiece.
- Positioning: cool-factor only. NOT a grind solution — `setSkillXp` already cheats skills instantly, which is what modders actually want.

### Other phase-2 items (shipped)
- **MCP server** (`McpServer.cs`): exposes the action set over local HTTP + JSON-RPC 2.0 (`initialize`, `tools/list`, `tools/call`). Tool calls are normalized to the executor's actions JSON and run on the main thread. Off by default (`EnableMcpServer`). v1 path stays direct OpenAI-compatible for latency.
- **Function-calling API style** (`ActionTools.cs`): OpenAI `tools` schema used instead of the single JSON blob when `UseFunctionCalling` is set; `tool_calls` are converted back into actions.
- **Streaming responses** (`AIBridge.AskStream` + `ModEntry.RenderStreaming`): SSE deltas type the message into a live HUD line, then the final message prints into the native chat box.
- **OBS stream-overlay panel** (`OverlayServer.cs`): browser source at `http://localhost:{OverlayPort}/` showing a live digest + last action + elf-active flag, polling `/data`.

### Still deferred
- Real walking farmhand sprite + obstacle pathfinding (A*). The elf currently moves in a straight line to each tile.

## Open-source packaging

- `config.json` git-ignored; ship `config.example.json`.
- README: Ollama install → `ollama pull qwen2.5:7b-instruct` → build → drop in `Mods/`.
- License file. No secrets anywhere.
```
