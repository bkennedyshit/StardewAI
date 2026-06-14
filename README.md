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
| `Stream` | `true` | Stream the model's reply live into a HUD line as it types. |
| `UseFunctionCalling` | `false` | Use OpenAI-style tool calling (`tools` array) instead of a single JSON blob. Disables streaming when on. |
| `EnableOverlay` | `true` | Serve a local OBS browser-source overlay (digest + last action). |
| `OverlayPort` | `8473` | Port for the overlay server (`http://localhost:8473/`). |
| `EnableMcpServer` | `false` | Expose the action set as a local MCP (JSON-RPC) server. |
| `McpPort` | `8474` | Port for the MCP server. |

## Phase 2 Features

- **Elf laborer.** Ask for a helper ("send an elf to water the east field", "have a farmhand harvest everything") and a Junimo-style helper walks your farm and does it tile-by-tile. The model assigns the task once; all the labor is deterministic C# (no AI in the loop). Chores: `water`, `harvest`, `refill`. Say "stop the elf" to cancel.
- **Streaming.** With `Stream` on, the AI's reply types out live in a HUD line, then prints into the chat box.
- **Function calling.** Set `UseFunctionCalling` to use the OpenAI `tools` protocol instead of the JSON blob.
- **OBS overlay.** With `EnableOverlay` on, add a Browser source in OBS pointed at `http://localhost:8473/` to show a live farm digest + the last action on stream.
- **MCP server.** With `EnableMcpServer` on, external agents can drive StardewAI's actions over local MCP (JSON-RPC 2.0) at `http://localhost:8474/` — `initialize`, `tools/list`, `tools/call`.

## Open Source
Built with open-source models in mind. Feel free to inspect, fork, or modify the code! 
All logic runs locally; no secrets, no telemetry.
