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
        
        // Chat history
        private List<object> ConversationHistory = new List<object>();

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
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith("/")) return true;

            // Optional: skip normal slash commands so they still work
            if (text.StartsWith("/")) return true;

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

            // Optional fallback hotkey
            if (Enum.TryParse<SButton>(Config.Hotkey, out var hotkey) && e.Button == hotkey)
            {
                // Triggering via hotkey opens the prompt (we'll implement full chat hook later in Wave 5)
                OpenChatUI("What should I do today?");
            }
        }

        private void OpenChatUI(string message)
        {
            var state = StateDigestBuilder.Build();
            string stateJson = JsonConvert.SerializeObject(state, Formatting.None);

            ConversationHistory.Add(new { role = "user", content = $"Current game state: {stateJson}\nPlayer request: {message}" });

            // Ensure history is capped
            if (ConversationHistory.Count > Config.HistoryTurns)
            {
                ConversationHistory.RemoveAt(0);
            }

            // Provide instructions as the system prompt (can be inserted before sending)
            var messagesToSend = new List<object>
            {
                new { role = "system", content = BuildSystemPrompt() }
            };
            messagesToSend.AddRange(ConversationHistory);

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

        private string BuildSystemPrompt()
        {
            return @"You are an AI assistant embedded inside Stardew Valley.
You receive the player's game state digest and their request.
ALWAYS respond with valid JSON in this exact format:
{
  ""reasoning"": ""Brief explanation of your thinking"",
  ""message"": ""What to show the player in-game (friendly, 1-2 sentences)"",
  ""actions"": [
    { ""type"": ""addItem"", ""itemId"": ""499"", ""quantity"": 10 },
    { ""type"": ""setWeather"", ""weather"": ""rain"" }
  ]
}

AVAILABLE ACTION TYPES:
- addItem (itemId, quantity) — itemId MUST be a real, existing Stardew Valley item id. Do not invent ids. Keep quantity reasonable (1-999); never give absurd amounts just because the player has a lot of money.
- removeItem (itemName)
- setWeather (sunny, rain, storm, snow)
- setSeason (spring, summer, fall, winter)
- addMoney (amount)
- setTime (time)
- setDay (day)
- warp (location)
- setSkillXp (skill, xp)
- setFriendship (npc, points)
- waterAllCrops ()
- clearCrops ()
- assignTask (chore: water|harvest|refill, area: {x,y,w,h} or ""farm"", repeat: true|false) — sends a Junimo-style elf helper to work a bounded area of the farm tile-by-tile. Use this when the player asks for a helper/farmhand/elf to water, harvest, or refill.
- cancelTask () — stops and removes the elf helper.
- message ()

Respond ONLY with the JSON object.";
        }
    }
}
