# StardewAI

StardewAI hooks Stardew Valley's native chat box into a locally-run LLM, letting you trigger instant cheats and actions by typing plain English. Press the chat key (**T** or **/**), type what you want, and the AI handles the rest. 

The model runs entirely on your own machine (using Ollama by default). The LLM is a tool-dispatcher into a bounded, validated action set — it maps natural language into game actions, so there is no lag or cloud API cost!

## Setup Instructions

### 1. Install Ollama
StardewAI works best with Ollama, a tool for running local LLMs.
1. Download and install [Ollama](https://ollama.com/).
2. Pull a model tuned for structured output / function calling. Open your terminal/command prompt and run:
   ```bash
   ollama pull hermes3:8b
   ```
   `hermes3:8b` is the default — it's tuned for JSON/tool-call output, which is exactly what this mod needs.

   *Other good picks (use what you have / fits your VRAM):*
   - `qwen2.5-coder:7b` — strong JSON adherence, ~4.7 GB
   - `llama3:8b` — solid general fallback
   - any small instruct model that can return JSON works; set it in `config.json`

3. **Make sure it runs on your GPU, not your CPU.** Local LLMs only feel instant on a GPU. After pulling, run these once so Ollama keeps the model on your GPU and resident in memory:
   ```bash
   # Keep the model loaded so it doesn't reload onto the CPU mid-session.
   # Windows (run once, then restart Ollama):
   setx OLLAMA_KEEP_ALIVE -1

   # Warm the model BEFORE launching the game (loads it onto the GPU):
   ollama run hermes3:8b ""

   # Verify placement — the PROCESSOR column should say GPU:
   ollama ps
   ```
   If `ollama ps` shows `CPU` despite having free VRAM, restart Ollama to force a fresh GPU load, and pick a model that comfortably fits your free VRAM (see the table below). StardewAI also auto-warms the model when your save loads (`WarmUpModel`), so the first request isn't a slow cold load.

### 2. Install the Mod
1. Ensure you have [SMAPI](https://smapi.io/) installed for Stardew Valley 1.6+.
2. Download the latest StardewAI release and extract it into your `Stardew Valley/Mods` folder.
3. Launch the game. SMAPI will generate a `config.json` file inside the `StardewAI` folder.

### 3. Usage
- Load your game.
- Press **T** (default chat key) or **F8** (configurable fallback key) to open the chat window.
- Type a request in plain English! (e.g., "Give me 10,000g and make it rain tomorrow", or "Warp me to the Saloon and boost my fishing skill").

## Configuration (`config.json`)

| Property | Default | Description |
|---|---|---|
| `BaseUrl` | `http://localhost:11434/v1` | URL for the OpenAI-compatible endpoint. Defaults to local Ollama. |
| `Model` | `hermes3:8b` | Model to use. Must match a model pulled in Ollama (`ollama list`). |
| `ApiKey` | `""` | Optional API key if using cloud/external providers instead of Ollama. |
| `Hotkey` | `F8` | Fallback key to trigger the AI chat prompt. |
| `Temperature` | `0.2` | Lower values are recommended to keep the AI output structured properly. |
| `MaxTokens` | `1024` | Max tokens in the model's reply. |
| `HistoryTurns` | `6` | How many recent message turns to keep for context. |
| `TimeoutSeconds` | `60` | How long to wait for the model before giving up. Generous so a cold model load doesn't fail. |
| `WarmUpModel` | `true` | Send one tiny background request when a save loads so the model is warm before your first request (avoids a cold-load timeout). |
| `Stream` | `true` | Stream the model's reply live into a HUD line as it types. |
| `UseFunctionCalling` | `false` | Use OpenAI-style tool calling (`tools` array) instead of a single JSON blob. Disables streaming when on. |
| `EnableOverlay` | `true` | Serve a local OBS browser-source overlay (digest + last action). |
| `OverlayPort` | `8473` | Port for the overlay server (`http://localhost:8473/`). |
| `EnableMcpServer` | `false` | Expose the action set as a local MCP (JSON-RPC) server. |
| `McpPort` | `8474` | Port for the MCP server. |
| `BlockedItemIds` | `[]` | Item ids the AI may never give you (bare like `"809"` or qualified like `"(O)809"`). Unknown/broken items are always rejected regardless. |

## Phase 2 Features

- **Elf laborer.** Ask for a helper ("send an elf to water the east field", "have a farmhand harvest everything") and a Junimo-style helper walks your farm and does it tile-by-tile. The model assigns the task once; all the labor is deterministic C# (no AI in the loop). Chores: `water`, `harvest`, `refill`. Say "stop the elf" to cancel.
- **Streaming.** With `Stream` on, the AI's reply types out live in a HUD line, then prints into the chat box.
- **Function calling.** Set `UseFunctionCalling` to use the OpenAI `tools` protocol instead of the JSON blob.
- **OBS overlay.** With `EnableOverlay` on, add a Browser source in OBS pointed at `http://localhost:8473/` to show a live farm digest + the last action on stream.
- **MCP server.** With `EnableMcpServer` on, external agents can drive StardewAI's actions over local MCP (JSON-RPC 2.0) at `http://localhost:8474/` — `initialize`, `tools/list`, `tools/call`.

## Requirements & Performance

StardewAI is a lightweight mod — it just sends a request to your local model and applies the result. **The actual compute happens in Ollama, not in the mod or the game.** What you need:

- **SMAPI** for Stardew Valley **1.6+**.
- **Ollama** (or any OpenAI-compatible endpoint) running locally with a model pulled.
- **A GPU is strongly recommended.** With a GPU that has enough free VRAM for your model, replies take ~1–3 seconds. The mod never blocks the game while waiting — requests run on a background thread — so a slow or missing model degrades gracefully instead of freezing your game.

Rough VRAM guide (model + a little headroom):

| Model | Approx. VRAM | Notes |
|---|---|---|
| `qwen2.5:3b-instruct` | ~3 GB | Leanest; great for 4–6 GB GPUs. |
| `hermes3:8b` (default) | ~6 GB | Best quality/reliability balance. |
| `qwen2.5-coder:7b` | ~5 GB | Very strong JSON adherence. |

**No capable GPU?** It still works on CPU, but expect replies to take much longer (tens of seconds), and Ollama will use your CPU cores during inference. If that's your situation, use a smaller model (`qwen2.5:3b-instruct`) and raise `TimeoutSeconds`.

## Troubleshooting

**"The AI model timed out" / first request fails.**
This is almost always a *cold load* — the model wasn't in memory yet, so the first request was slow. StardewAI warms the model up automatically when your save loads (`WarmUpModel`), which usually prevents this. If it still happens, just try again (it'll be warm now), or raise `TimeoutSeconds` in `config.json`.

**The model is running on my CPU even though I have a GPU.**
That's an Ollama placement decision, not the mod. Check where it loaded:
```bash
ollama ps
```
The `PROCESSOR` column should say `GPU`. If it says `CPU` despite having free VRAM, it usually means the model loaded during a momentary VRAM spike and Ollama committed to CPU. Fixes:
- Load/warm the model **before** launching the game, or restart Ollama to force a fresh load.
- Keep the model resident so it doesn't reload mid-session:
  ```bash
  # Windows (then restart Ollama)
  setx OLLAMA_KEEP_ALIVE -1
  ```
- Use a model that comfortably fits your free VRAM (see the table above).

**"Couldn't reach the AI model."**
Ollama isn't running or `BaseUrl` is wrong. Start Ollama and confirm `ollama list` shows your model. The default `BaseUrl` is `http://localhost:11434/v1`.

**The AI tried to give me a broken/invalid item.**
StardewAI validates every item before spawning it and refuses unknown or "error" items, so they never end up in your inventory or dropped on the ground. To block a *specific* valid item too, add its id to `BlockedItemIds` in `config.json`.

## Open Source
Built with open-source models in mind. Feel free to inspect, fork, or modify the code! 
All logic runs locally; no secrets, no telemetry.
