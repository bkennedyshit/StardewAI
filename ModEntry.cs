using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using Newtonsoft.Json;
using System.Threading.Tasks;
using HarmonyLib;

namespace StardewAI
{
    public class ModEntry : Mod
    {
        private ModConfig Config;
        private AIBridge Bridge;
        
        // Expose instance for Harmony patches
        internal static ModEntry Instance;

        // The queue connecting the background task to the main game thread
        private ConcurrentQueue<string> ActionQueue = new ConcurrentQueue<string>();
        
        // Chat history (user/assistant pairs; touched from the main thread and the request task)
        private List<object> ConversationHistory = new List<object>();
        private readonly object HistoryLock = new object();

        // Streaming state (written from background task, read on main thread)
        private volatile string StreamLiveText = null;
        private volatile bool StreamActive = false;
        private HUDMessage StreamHud = null;

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            Config = helper.ReadConfig<ModConfig>();
            Bridge = new AIBridge(Config, Monitor);
            ActionExecutor.Init(Monitor, Config.BlockedItemIds);
            TaskRunner.Init(Monitor);

            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

            var harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(ChatBox), nameof(ChatBox.textBoxEnter), new Type[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(ModEntry.OnChatBoxEnter))
            );

            Monitor.Log($"StardewAI loaded. Native chat hook active, fallback hotkey {Config.Hotkey}.", LogLevel.Info);

            if (Config.EnableOverlay)
                OverlayServer.Start(Config.OverlayPort, Monitor);
            if (Config.EnableMcpServer)
                McpServer.Start(Config.McpPort, Monitor, json => ActionQueue.Enqueue(json));
        }

        public static bool OnChatBoxEnter(ChatBox __instance, string text_to_send)
        {
            if (!Context.IsWorldReady) return true; // run original

            string text = text_to_send?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith("/")) return true; // slash commands still work

            // Trigger AI flow and swallow the message so it doesn't show in vanilla chat
            // (Or we can let it show, but swallow avoids multiplayer broadcast of commands)
            Instance.OpenChatUI(text);

            // Clear the textbox and close the chat box
            __instance.chatBox.Text = "";
            Game1.closeTextEntry();
            return false; // skip original method
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            // Warm the model in the background so the first real request isn't a slow cold load.
            if (Config.WarmUpModel)
            {
                Monitor.Log("Warming up the AI model in the background...", LogLevel.Trace);
                Task.Run(() => Bridge.WarmUp());
            }
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            // Free the model from memory when leaving a save (it re-warms on the next SaveLoaded).
            if (Config.WarmUpModel)
                Task.Run(() => Bridge.UnloadModel());
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // Drain the queue on the main game thread
            while (ActionQueue.TryDequeue(out string result))
            {
                if (!string.IsNullOrEmpty(result))
                {
                    ActionExecutor.Execute(result);
                }
            }

            // Drive the deterministic elf laborer (phase 2) on the main thread.
            TaskRunner.Update();

            // Render streamed AI text live (phase 2).
            RenderStreaming();

            // Refresh the OBS overlay snapshot ~twice a second (main thread reads Game1 safely).
            if (Config.EnableOverlay && Context.IsWorldReady && e.IsMultipleOf(30))
            {
                try
                {
                    OverlayServer.UpdateSnapshot(StateDigestBuilder.Build(), ActionExecutor.LastAction);
                }
                catch { /* snapshot best-effort */ }
            }

            // Keep the model loaded while playing (re-pin every ~2 min, under the 5-min session
            // keep-alive) so it never drops out mid-session. When the game closes, these stop and
            // Ollama unloads the model on its own.
            if (Config.WarmUpModel && Context.IsWorldReady && e.IsMultipleOf(7200))
            {
                Task.Run(() => Bridge.WarmUp());
            }
        }

        private void RenderStreaming()
        {
            if (StreamActive)
            {
                string live = ExtractLiveMessage(StreamLiveText);
                if (string.IsNullOrEmpty(live))
                    live = "AI is typing...";

                if (StreamHud == null)
                {
                    StreamHud = new HUDMessage(live, HUDMessage.newQuest_type) { noIcon = true };
                    Game1.addHUDMessage(StreamHud);
                }
                else
                {
                    StreamHud.message = live;
                }
                StreamHud.timeLeft = 5000f; // keep alive while streaming
            }
            else if (StreamHud != null)
            {
                // Stream finished; let the executor post the final message.
                StreamHud.timeLeft = 0f;
                StreamHud = null;
            }
        }

        /// <summary>Pull the partial "message" field out of streaming JSON so the player sees readable text.</summary>
        private static string ExtractLiveMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            var m = System.Text.RegularExpressions.Regex.Match(
                raw, "\"message\"\\s*:\\s*\"(.*?)(?:\"|$)",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success && m.Groups[1].Value.Length > 0)
                return m.Groups[1].Value.Replace("\\n", " ").Replace("\\\"", "\"");

            return null;
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            // Optional fallback hotkey: opens the native chat box (same flow as pressing T).
            if (Enum.TryParse<SButton>(Config.Hotkey, out var hotkey) && e.Button == hotkey)
            {
                if (Game1.activeClickableMenu == null && Game1.chatBox != null && !Game1.chatBox.isActive())
                {
                    Game1.chatBox.activate();
                    Helper.Input.Suppress(e.Button);
                }
            }
        }

        private void OpenChatUI(string message)
        {
            // 1) Instant path: common requests are parsed locally — no model call, no CPU/GPU load.
            if (Config.EnableQuickCommands)
            {
                string? quick = null;
                try { quick = QuickCommands.TryParse(message); }
                catch (Exception ex) { Monitor.Log($"Quick command parse error: {ex.Message}", LogLevel.Trace); }

                if (quick != null)
                {
                    Monitor.Log($"Quick command handled without the model: \"{message}\"", LogLevel.Debug);
                    AddHistory(message, quick);
                    ActionQueue.Enqueue(quick);
                    return;
                }
            }

            // 2) Model path. Only the CURRENT message carries the state digest; history keeps just
            //    the short request/reply text. Much smaller prompt = much faster on CPU-only PCs.
            var state = StateDigestBuilder.Build();
            string stateJson = JsonConvert.SerializeObject(state, Formatting.None);

            var messagesToSend = new List<object>
            {
                new { role = "system", content = BuildSystemPrompt() }
            };
            lock (HistoryLock)
                messagesToSend.AddRange(ConversationHistory);
            messagesToSend.Add(new { role = "user", content = $"Current game state: {stateJson}\nPlayer request: {message}" });

            bool useStreaming = Config.Stream && !Config.UseFunctionCalling;
            if (!useStreaming)
                Game1.addHUDMessage(new HUDMessage("AI is thinking...", HUDMessage.newQuest_type));
            else
                StreamActive = true;

            Task.Run(async () =>
            {
                string result;
                try
                {
                    if (useStreaming)
                        result = await Bridge.AskStream(messagesToSend, text => StreamLiveText = text);
                    else
                        result = await Bridge.Ask(messagesToSend);
                }
                finally
                {
                    StreamActive = false;
                }

                if (result != null)
                {
                    AddHistory(message, result);
                    // Enqueue the JSON response to be executed on the main thread
                    ActionQueue.Enqueue(result);
                }
                else
                {
                    // Surface an honest, specific reason (timeout vs unreachable vs other).
                    string err = Bridge.LastError ?? "The AI request failed.";
                    var errJson = new Newtonsoft.Json.Linq.JObject { ["message"] = err }.ToString();
                    ActionQueue.Enqueue(errJson);
                }
            });
        }

        /// <summary>Record one user/assistant exchange, keeping at most HistoryTurns exchanges.</summary>
        private void AddHistory(string userText, string assistantJson)
        {
            lock (HistoryLock)
            {
                ConversationHistory.Add(new { role = "user", content = $"Player request: {userText}" });
                ConversationHistory.Add(new { role = "assistant", content = assistantJson });

                int maxMessages = Math.Max(0, Config.HistoryTurns) * 2;
                while (ConversationHistory.Count > maxMessages && ConversationHistory.Count >= 2)
                    ConversationHistory.RemoveRange(0, 2);
            }
        }

        private string BuildSystemPrompt()
        {
            return @"You are an AI assistant embedded inside Stardew Valley.
You receive the player's game state digest and their request.
ALWAYS respond with valid JSON in this exact format:
{
  ""reasoning"": ""Brief explanation of your thinking"",
  ""message"": ""What to show the player in-game (friendly, 1-2 sentences)"",
  ""actions"": [
    { ""type"": ""addItem"", ""itemName"": ""Iridium Bar"", ""quantity"": 5 },
    { ""type"": ""setSkillLevel"", ""skill"": ""all"", ""level"": 10 },
    { ""type"": ""setWeather"", ""weather"": ""rain"" }
  ]
}

AVAILABLE ACTION TYPES:
- addItem (itemName, quantity) — use the item's English NAME exactly as the player said it (e.g. ""Prismatic Shard"", ""Diamond"", ""Parsnip Seeds""). Do NOT use numeric ids; the mod looks up the real item. Quantity 1-999.
- removeItem (itemName, quantity)
- setWeather (weather: sunny|rain|storm|snow)
- setSeason (season: spring|summer|fall|winter)
- addMoney (amount)
- setTime (time: 600-2600, e.g. 1300 = 1pm)
- setDay (day: 1-28)
- warp (location) — common names like Farm, Town, Beach, Mountain, Forest, Saloon, Ginger Island, Marnie's Ranch, Spa, Desert, Mines.
- setSkillLevel (skill: farming|fishing|foraging|mining|combat|all, level: 0-10) — use this for ""level 10"", ""max my skills"", etc. Use skill ""all"" for all skills; do NOT list skills one by one.
- setSkillXp (skill, xp) — only when the player asks for a specific amount of XP.
- setFriendship (npc, hearts) — set friendship to that many hearts (0-10). Or (npc, points) to add points; 250 points = 1 heart.
- waterAllCrops ()
- clearCrops ()
- assignTask (chore: water|harvest|refill, area: {x,y,w,h} or ""farm"", repeat: true|false) — sends an elf helper to work the farm. Use for helper/farmhand/elf requests.
- cancelTask () — stops the elf helper.
- message () — just talk, change nothing.

RULES:
- Respond ONLY with the JSON object.
- When the player asks for something that maps to an action, ALWAYS include that action. Do not refuse or skip it.
- Use the exact item name and quantity the player asked for. Never substitute a different item.
- For warp requests, ALWAYS emit a warp action, even if you think the player is already there.";
        }
    }
}
