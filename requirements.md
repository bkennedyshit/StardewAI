# Requirements Document

## Introduction

StardewAI is a SMAPI mod that wires Stardew Valley's native, normally-useless single-player chat box into a locally-run LLM, letting players trigger instant cheats and actions by typing plain English. The model runs on the player's own machine (Ollama by default), acts purely as a tool-dispatcher into a bounded, validated action set, and never decides strategy. This document defines the requirements; see `design.md` and `tasks.md` for the implementation.

## Glossary

- **SMAPI** — Stardew Modding API; the runtime that loads C# mods.
- **Chat box** — Stardew's built-in text input opened with **T** or **/**, functional even in single-player.
- **Dispatcher** — the LLM's role here: map natural language to a validated action, not to reason or plan.
- **Action** — a typed, schema-defined operation the mod validates and executes (e.g. `addMoney`, `setWeather`).
- **Digest** — a compact summary of game state sent to the model instead of raw dumps.
- **Elf / Player 2** — phase-2 optional Junimo-style laborer that performs bounded farm chores; not in v1.
- **Action queue** — thread-safe buffer that moves AI results onto the main game thread for mutation.

## The premise (read this first)

Stardew's chat box (the **T** key, also **/**) works in single-player — but there's no one to talk to. It's a dormant interface shipped with every copy of the game. **This mod plugs that empty chat box into a locally-run LLM.** You press the key you already know, type plain English, and the game does it.

That chat layer IS the product. Everything else hangs off it.

## What it's for

Modders cheat. They don't grind, and they don't want to. So the payload behind the chat box is **instant cheats via natural language** — money, items, skills, weather, time, warp, spawns. Type "max my farming and give me 50k", it happens. No menu hunting, no item-id lookup, no console commands.

The whole stack — game and model — runs on the player's own machine. No cloud, no API key required, free, open source.

## Product thesis

**Small local models are sufficient when the LLM is a tool-dispatcher, not a reasoner.** The game state is structured and the action set is bounded, so the model only maps language → a validated tool call. The C# layer owns all correctness. This is why it runs fine on a 3B–7B local model alongside the game.

## Priority hierarchy (do not invert)

1. **The hook** — native chat box (T) wired to a local LLM. The identity of the whole mod.
2. **The payload** — instant cheat/action dispatch in natural language. The meat. This is what ships and what gets used.
3. **The flex (optional, phase 2)** — a "Player 2" elf laborer that does bounded farm chores. Pure cool-factor / stream moment. NOT a grinding solution (modders cheat skills directly), NOT required for v1, NOT the selling point.

## Goals

- The native chat box (T / configurable) is the primary interface — no custom menu required for core use.
- Run entirely locally against an OpenAI-compatible endpoint (Ollama default, LM Studio / cloud optional).
- Be reliable on a 3B–8B parameter model via schema-enforced structured output.
- Make instant cheats effortless: natural language → validated action, no ids or syntax.
- Execute every action safely, validating before mutating game state.
- Never crash the game (thread-safe mutation, fault-tolerant parsing).
- Be open-source-ready: no secrets in the repo, clear config, documented model setup.

## Non-Goals (v1)

- Autonomous play (AI decides strategy, pathfinds the whole map, plans town trips). This is the crowded, hard, big-model race — explicitly not our lane.
- The Player 2 elf laborer (deferred to phase 2; cool-factor only, since cheating skills directly is what modders actually want).
- Multiplayer sync of AI actions.
- Fine-tuning a custom model.

## Personas

- **Streamer/creator (primary).** Wants a visually compelling, fully-local demo to show off small-model tooling and an open-source project.
- **Casual cheater.** Wants to type "give me 10k and make it rain" and have it happen.
- **Tinkerer.** Wants to swap models, add actions, point at their own endpoint.

## Requirements

### R1 — Local-first LLM connectivity
**User story:** As a player, I want the mod to talk to a local model so I pay nothing and leak nothing.

Acceptance criteria:
1. WHEN the mod sends a request THEN it SHALL POST to an OpenAI-compatible `/v1/chat/completions` endpoint configured in `config.json`.
2. The default base URL SHALL be `http://localhost:11434/v1` (Ollama).
3. WHEN no API key is configured THEN the request SHALL still succeed against a local endpoint (no auth header required).
4. WHEN an API key is configured THEN it SHALL be sent as a Bearer token.
5. The model id SHALL be read from config (default a small local model, e.g. `qwen2.5:7b-instruct`), never hardcoded to a nonexistent id.

### R2 — Schema-enforced structured output
**User story:** As a developer, I want the model output to be valid so a small model is reliable.

Acceptance criteria:
1. The request SHALL instruct the endpoint to return JSON conforming to the defined action schema (via `response_format`/`format` where supported).
2. WHEN the model returns text that is not valid JSON THEN the mod SHALL fail gracefully (log + HUD "couldn't understand that") and SHALL NOT crash or mutate state.
3. The schema SHALL define: `reasoning` (string), `message` (string), `actions` (array of typed action objects).

### R3 — Compact game-state digest
**User story:** As a developer, I want a small prompt so small models stay accurate.

Acceptance criteria:
1. State sent to the model SHALL be a summarized digest, not raw dumps (season, day, year, time, weather, money, energy/maxEnergy, crop summary counts, notable inventory).
2. Inventory SHALL be capped/summarized to avoid flooding context.
3. The digest SHALL be human-readable so it doubles as stream overlay content.

### R4 — Native chat box as the interface
**User story:** As a player, I want to use the chat box I already know (T) to talk to the AI.

Acceptance criteria:
1. The mod SHALL hook Stardew's native chat box (default **T**, also **/**) as the primary input. WHEN the player opens chat and submits a message THEN the mod SHALL route it to the LLM with the current state digest and recent history.
2. A configurable fallback hotkey (default F8) MAY open the same flow, but the native chat box SHALL work without a custom menu.
3. The mod SHALL retain the last N (default 6) message turns for context.
4. The AI `message` SHALL be printed back into the chat box / HUD; reasoning SHALL be logged, not shown by default.
5. Requests SHALL run off the game thread so the game does not freeze while waiting.
6. The mod SHALL distinguish AI messages from normal chat (e.g. a prefix or the message routes only when the world is loaded).

### R5 — Safe action execution
**User story:** As a player, I want actions to actually happen, without crashing my save.

Acceptance criteria:
1. ALL game-state mutations SHALL occur on the main game thread (action queue drained on update tick).
2. Each action SHALL be validated before execution (item id resolves, enum values legal, numeric bounds sane).
3. WHEN an action is invalid THEN it SHALL be skipped with a logged warning; other actions SHALL still run.
4. The executor SHALL be initialized with a Monitor before use (no null-ref).

### R6 — Action set (v1)
The mod SHALL support at least these validated actions:
1. `addItem` (itemId, quantity), `removeItem` (name/qty)
2. `addMoney` (signed amount, clamped ≥ 0)
3. `setWeather` (sunny/rain/storm/snow) using 1.6 APIs
4. `setSeason` (spring/summer/fall/winter) using 1.6 `Game1.season`
5. `setTime` (clamped to valid range), `setDay`
6. `warp` (named location)
7. `setSkillXp` / `setFriendship` (bounded)
8. `waterAllCrops`, `clearCrops`
9. `message` (no-op, advice only)

### R7 — Resilience & UX
Acceptance criteria:
1. WHEN the endpoint is unreachable THEN the mod SHALL show a clear HUD error and keep running.
2. Requests SHALL time out (default 30s) rather than hang.
3. WHEN the world is not loaded THEN the chat hotkey SHALL be inert.

### R8 — Open-source readiness
Acceptance criteria:
1. The repo SHALL contain no API keys or secrets; `config.json` SHALL be git-ignored with a documented example.
2. README SHALL document model setup (Ollama install, `ollama pull <model>`), config, and a recommended low-VRAM model list.
3. License SHALL be specified.

### R9 — Player 2 elf laborer (PHASE 2 — SHIPPED)
**User story:** As a player, I want to optionally assign a little helper bounded farm chores for the cool factor — not because I need to grind.

Acceptance criteria:
1. The elf SHALL be assigned a bounded chore over a defined tile area via a `assignTask` action (chore, area, repeat) issued through the chat box. ✅ (`cancelTask` stops it)
2. Chore execution SHALL be deterministic C# (Junimo-style: water/harvest/refill loops over a region), with NO LLM calls during execution. The model fires once to assign the task. ✅ (`TaskRunner`)
3. v1 MAY ship without this entirely; it SHALL NOT block or complicate the cheat dispatch path. ✅ (gated behind the `assignTask` action; the dispatch path is unchanged)
4. This is cool-factor / stream-moment only. It is NOT positioned as a grinding solution — skill cheats (R6.7) already cover that instantly.

Still deferred: real walking farmhand sprite + obstacle pathfinding (the helper currently moves straight-line to each tile).

## Prior art & differentiation

Existing AI-Stardew projects (verified): **Stardew Valley Companions / amarisaster-stardewvalley-mcp** (MCP-driven autonomous NPC companions), **Hunter-Thompson/stardew-mcp** (goal-driven WebSocket agent with ASCII-map pathfinding and a `cheat_mode`), **"Gary the AI"/Snappy** (live map knowledge, autonomous town trips), **phin01/AutoPlaySV** (rule-based, no LLM), **Automate** (logic loops, no LLM).

They all chase **autonomous play** driven by a **big external/cloud model over MCP/WebSocket**. StardewAI is deliberately different on three axes:
1. **Interface:** the game's own native chat box (T) is the product, not a custom agent harness.
2. **Locality:** fully local small model (3B–7B) co-resident with the game — offline, free, no key. The others default to cloud frontier models.
3. **Role:** the model is a one-shot dispatcher into validated cheats, not a strategist driving every step. This is what keeps VRAM/latency trivial and reliability high.

We are not racing autonomy. We own "the native chat box, powered locally, that just does what you type."

## Success Metrics (stream/business)

- Runs game + model on a single consumer GPU with no cloud calls.
- A 3B–7B model executes common requests correctly ≥ ~90% of the time thanks to schema enforcement + validation.
- Cold "type a sentence → action happens" latency is a couple seconds or less on a local small model.
- The hook reads instantly to viewers: press the chat key everyone knows, type English, watch it happen.
