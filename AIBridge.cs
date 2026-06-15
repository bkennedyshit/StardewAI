using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;

namespace StardewAI
{
    public class AIBridge
    {
        private readonly ModConfig Config;
        private readonly IMonitor Monitor;
        private readonly HttpClient Http;

        /// <summary>
        /// A player-facing description of the most recent failure (timeout vs. unreachable vs. other),
        /// or null if the last request succeeded. ModEntry shows this in the HUD.
        /// </summary>
        public string? LastError { get; private set; }

        public AIBridge(ModConfig config, IMonitor monitor)
        {
            Config = config;
            Monitor = monitor;

            Http = new HttpClient();
            Http.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

            if (!string.IsNullOrWhiteSpace(config.ApiKey))
            {
                Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            }
        }

        private string Url => Config.BaseUrl.TrimEnd('/') + "/chat/completions";

        /// <summary>Map an exception to an honest, actionable player-facing message.</summary>
        private string DescribeError(Exception ex)
        {
            switch (ex)
            {
                case TaskCanceledException _:
                    return $"The AI model timed out after {Config.TimeoutSeconds}s — it may still be loading. Try again in a few seconds.";
                case HttpRequestException _:
                    return "Couldn't reach the AI model. Is Ollama running? (check the BaseUrl in config.json)";
                default:
                    return "The AI request failed unexpectedly. See the SMAPI log for details.";
            }
        }

        /// <summary>
        /// Non-streaming request. Returns the executor-ready JSON string (a {reasoning,message,actions}
        /// blob), or null on failure. Handles both single-JSON-blob and function-calling modes.
        /// </summary>
        public async Task<string> Ask(List<object> messages)
        {
            object body = BuildBody(messages, stream: false);
            var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            try
            {
                var response = await Http.PostAsync(Url, content);
                var responseStr = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Monitor.Log($"API error ({response.StatusCode}): {responseStr}", LogLevel.Error);
                    LastError = $"The AI model returned an error ({(int)response.StatusCode}). Is the model name in config.json correct?";
                    return null;
                }

                var parsed = JObject.Parse(responseStr);
                var message = parsed["choices"]?[0]?["message"] as JObject;
                if (message == null)
                {
                    LastError = "The AI model returned an empty response.";
                    return null;
                }

                LastError = null;

                // Function-calling mode: convert tool_calls into our actions array.
                if (Config.UseFunctionCalling && message["tool_calls"] is JArray)
                    return NormalizeToolCalls(message);

                return message["content"]?.ToString();
            }
            catch (Exception ex)
            {
                LastError = DescribeError(ex);
                Monitor.Log($"AIBridge request failed: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Streaming request (single-JSON-blob mode only). Invokes <paramref name="onDelta"/> with the
        /// accumulated text as tokens arrive, and returns the full content string when complete.
        /// </summary>
        public async Task<string> AskStream(List<object> messages, Action<string> onDelta)
        {
            object body = BuildBody(messages, stream: true);
            var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    Monitor.Log($"API error ({response.StatusCode}): {err}", LogLevel.Error);
                    LastError = $"The AI model returned an error ({(int)response.StatusCode}). Is the model name in config.json correct?";
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                var sb = new StringBuilder();
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:"))
                        continue;

                    string data = line.Substring("data:".Length).Trim();
                    if (data == "[DONE]")
                        break;

                    try
                    {
                        var chunk = JObject.Parse(data);
                        string delta = chunk["choices"]?[0]?["delta"]?["content"]?.ToString();
                        if (!string.IsNullOrEmpty(delta))
                        {
                            sb.Append(delta);
                            onDelta?.Invoke(sb.ToString());
                        }
                    }
                    catch
                    {
                        // Ignore malformed/keepalive chunks.
                    }
                }

                LastError = null;
                return sb.ToString();
            }
            catch (Exception ex)
            {
                LastError = DescribeError(ex);
                Monitor.Log($"AIBridge stream request failed: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>True if the endpoint looks like a local Ollama (default port 11434).</summary>
        private bool LooksLikeOllama => Config.BaseUrl.Contains(":11434");

        /// <summary>
        /// Keep-alive (seconds) used while a save is loaded. Finite on purpose: the mod re-pins
        /// every couple minutes while you play, so the model stays loaded for the session — but once
        /// the game closes and the re-pins stop, Ollama unloads it on its own, freeing your VRAM.
        /// </summary>
        private const int SessionKeepAliveSeconds = 300;

        /// <summary>The Ollama native API base (BaseUrl without the trailing /v1).</summary>
        private string OllamaBase()
        {
            string b = Config.BaseUrl.TrimEnd('/');
            if (b.EndsWith("/v1"))
                b = b.Substring(0, b.Length - "/v1".Length);
            return b.TrimEnd('/');
        }

        /// <summary>
        /// Load the model and keep it resident for the session. Called when a save loads and
        /// re-called periodically while playing so it never idle-unloads mid-session.
        /// </summary>
        public Task WarmUp() => SetKeepAlive(SessionKeepAliveSeconds, "warm-up", 15);

        /// <summary>
        /// Unload the model from memory immediately (keep_alive = 0) to free VRAM when leaving a
        /// save. Short timeout so it never blocks.
        /// </summary>
        public Task UnloadModel() => SetKeepAlive(0, "unload", 3);

        /// <summary>
        /// Set the model's keep_alive via Ollama's native /api/generate (which reliably honors it).
        /// Fully fire-and-forget; failures are only logged at trace level. No-op for non-Ollama
        /// endpoints (their /api/generate won't exist, which is caught and ignored).
        /// </summary>
        private async Task SetKeepAlive(int keepAlive, string label, int timeoutSeconds)
        {
            try
            {
                string url = OllamaBase() + "/api/generate";
                var body = new Dictionary<string, object>
                {
                    ["model"] = Config.Model,
                    ["prompt"] = "",
                    ["keep_alive"] = keepAlive,
                    ["stream"] = false
                };
                var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var response = await Http.PostAsync(url, content, cts.Token);
                Monitor.Log($"Model {label} (keep_alive={keepAlive}) -> {(int)response.StatusCode}.", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Model {label} skipped: {ex.GetType().Name}", LogLevel.Trace);
            }
        }

        // ----- request building -----

        private object BuildBody(List<object> messages, bool stream)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = Config.Model,
                ["temperature"] = Config.Temperature,
                ["max_tokens"] = Config.MaxTokens,
                ["messages"] = messages,
                ["stream"] = stream
            };

            if (Config.UseFunctionCalling)
            {
                // OpenAI-style tools. Streaming tool-calls is intentionally not used; callers
                // disable streaming when function-calling is on.
                body["tools"] = ActionTools.Schema;
                body["tool_choice"] = "auto";
            }
            else
            {
                // Constrain Ollama / compatible endpoints to emit JSON.
                body["format"] = "json";
            }

            // For local Ollama, keep the model loaded for the session so a normal request doesn't
            // shorten the idle-unload timer. (Ignored by other endpoints.)
            if (LooksLikeOllama)
                body["keep_alive"] = SessionKeepAliveSeconds;

            return body;
        }

        // ----- function-calling normalization -----

        private string NormalizeToolCalls(JObject message)
        {
            var actions = new JArray();
            if (message["tool_calls"] is JArray toolCalls)
            {
                foreach (var tc in toolCalls)
                {
                    string name = tc["function"]?["name"]?.ToString();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    string argsStr = tc["function"]?["arguments"]?.ToString();
                    JObject args;
                    try
                    {
                        args = string.IsNullOrWhiteSpace(argsStr) ? new JObject() : JObject.Parse(argsStr);
                    }
                    catch
                    {
                        args = new JObject();
                    }

                    args["type"] = name;
                    actions.Add(args);
                }
            }

            var result = new JObject
            {
                ["message"] = message["content"]?.ToString() ?? "",
                ["actions"] = actions
            };
            return result.ToString();
        }
    }
}
