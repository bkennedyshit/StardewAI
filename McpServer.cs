using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;

namespace StardewAI
{
    /// <summary>
    /// Exposes StardewAI's bounded action set as a local MCP (Model Context Protocol) server over
    /// HTTP + JSON-RPC 2.0. This lets external agents / the Companions-style ecosystem drive the
    /// same validated actions while everything still runs locally.
    ///
    /// Supported methods: initialize, tools/list, tools/call.
    /// Tool calls are normalized into the executor's actions JSON and handed to the main-thread queue.
    /// </summary>
    public static class McpServer
    {
        private const string ProtocolVersion = "2024-11-05";

        private static IMonitor Monitor;
        private static HttpListener Listener;
        private static Action<string> Enqueue;   // hands normalized action JSON to the main-thread queue
        private static bool Running;

        public static void Start(int port, IMonitor monitor, Action<string> enqueue)
        {
            Monitor = monitor;
            Enqueue = enqueue;
            if (Running)
                return;

            try
            {
                Listener = new HttpListener();
                Listener.Prefixes.Add($"http://localhost:{port}/");
                Listener.Start();
                Running = true;
                Monitor.Log($"MCP server listening at http://localhost:{port}/ (JSON-RPC 2.0)", LogLevel.Info);
                Task.Run(ListenLoop);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to start MCP server on port {port}: {ex.Message}", LogLevel.Warn);
                Running = false;
            }
        }

        public static void Stop()
        {
            Running = false;
            try { Listener?.Stop(); Listener?.Close(); }
            catch { /* ignore */ }
            Listener = null;
        }

        private static async Task ListenLoop()
        {
            while (Running && Listener != null)
            {
                HttpListenerContext ctx;
                try { ctx = await Listener.GetContextAsync(); }
                catch { break; }

                try { HandleRequest(ctx); }
                catch (Exception ex) { Monitor?.Log($"MCP request error: {ex.Message}", LogLevel.Trace); }
            }
        }

        private static void HandleRequest(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                Write(ctx, "{\"status\":\"StardewAI MCP server. POST JSON-RPC 2.0 here.\"}");
                return;
            }

            string requestBody;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                requestBody = reader.ReadToEnd();

            JObject req;
            try { req = JObject.Parse(requestBody); }
            catch
            {
                Write(ctx, Error(null, -32700, "Parse error"));
                return;
            }

            JToken id = req["id"];
            string method = req["method"]?.ToString();
            JObject prms = req["params"] as JObject;

            switch (method)
            {
                case "initialize":
                    Write(ctx, Result(id, new JObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "StardewAI", ["version"] = "0.2.0" }
                    }));
                    break;

                case "notifications/initialized":
                    // Notification: no response required.
                    Write(ctx, "");
                    break;

                case "tools/list":
                    Write(ctx, Result(id, new JObject { ["tools"] = BuildMcpTools() }));
                    break;

                case "tools/call":
                    HandleToolCall(ctx, id, prms);
                    break;

                default:
                    Write(ctx, Error(id, -32601, $"Method not found: {method}"));
                    break;
            }
        }

        private static void HandleToolCall(HttpListenerContext ctx, JToken id, JObject prms)
        {
            string name = prms?["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                Write(ctx, Error(id, -32602, "Missing tool name"));
                return;
            }

            JObject args = prms["arguments"] as JObject ?? new JObject();
            args["type"] = name;

            // Wrap as the executor's action JSON and queue for main-thread execution.
            var actionJson = new JObject
            {
                ["message"] = "",
                ["actions"] = new JArray { args }
            }.ToString();

            Enqueue?.Invoke(actionJson);
            Monitor?.Log($"MCP tools/call -> queued action '{name}'.", LogLevel.Info);

            Write(ctx, Result(id, new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Queued action '{name}' for execution in Stardew Valley."
                    }
                },
                ["isError"] = false
            }));
        }

        /// <summary>Convert the OpenAI-style tools schema into MCP tool descriptors.</summary>
        private static JArray BuildMcpTools()
        {
            var tools = new JArray();
            foreach (var tool in ActionTools.Schema)
            {
                var fn = tool["function"];
                if (fn == null)
                    continue;

                tools.Add(new JObject
                {
                    ["name"] = fn["name"],
                    ["description"] = fn["description"],
                    ["inputSchema"] = fn["parameters"]
                });
            }
            return tools;
        }

        // ----- JSON-RPC helpers -----

        private static string Result(JToken? id, JObject result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = result
            }.ToString();
        }

        private static string Error(JToken? id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            }.ToString();
        }

        private static void Write(HttpListenerContext ctx, string body)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
