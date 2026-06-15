using System.Collections.Generic;

namespace StardewAI
{
    public class ModConfig
    {
        public string BaseUrl { get; set; } = "http://localhost:11434/v1";
        public string Model { get; set; } = "hermes3:8b";
        public string ApiKey { get; set; } = "";
        public string Hotkey { get; set; } = "F8";
        public double Temperature { get; set; } = 0.2;
        public int MaxTokens { get; set; } = 1024;
        public int HistoryTurns { get; set; } = 6;

        /// <summary>
        /// How long to wait for the model before giving up. A cold model load (first request after
        /// the model isn't resident yet) can be slow, so this is generous by default.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Send one tiny background request when a save loads, so the model is warm (loaded into
        /// memory) before the player's first real request — avoids a cold-load timeout. Costs one
        /// trivial request per session. Set false to disable.
        /// </summary>
        public bool WarmUpModel { get; set; } = true;

        // --- Phase 2 options ---

        /// <summary>Stream the model response live into the chat box as it is generated.</summary>
        public bool Stream { get; set; } = true;

        /// <summary>Use OpenAI-style function/tool calling (tools array) instead of a single JSON blob.</summary>
        public bool UseFunctionCalling { get; set; } = false;

        /// <summary>Serve a local OBS stream-overlay panel (digest + last action).</summary>
        public bool EnableOverlay { get; set; } = true;

        /// <summary>Port for the OBS overlay HTTP server.</summary>
        public int OverlayPort { get; set; } = 8473;

        /// <summary>Expose the action set as a local MCP (JSON-RPC over HTTP) server.</summary>
        public bool EnableMcpServer { get; set; } = false;

        /// <summary>Port for the MCP server.</summary>
        public int McpPort { get; set; } = 8474;

        /// <summary>
        /// Item ids the AI is never allowed to give you (qualified like "(O)809" or bare like "809").
        /// Use this to block specific items you don't want spawned. Invalid/unknown/error items are
        /// always rejected regardless of this list.
        /// </summary>
        public List<string> BlockedItemIds { get; set; } = new List<string>();
    }
}
