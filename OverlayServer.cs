using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using StardewModdingAPI;

namespace StardewAI
{
    /// <summary>
    /// Lightweight local HTTP server that serves an OBS browser-source overlay: the current
    /// game-state digest plus the last action StardewAI took. The page polls /data for live updates.
    ///
    /// Threading: the game-state snapshot is built on the main thread (see ModEntry) and cached as a
    /// JSON string here; the HTTP handler only ever reads that cached string, never touches Game1.
    /// </summary>
    public static class OverlayServer
    {
        private static IMonitor Monitor;
        private static HttpListener Listener;
        private static volatile string SnapshotJson = "{}";
        private static bool Running;

        /// <summary>Cache the latest digest + last action. Called from the main thread.</summary>
        public static void UpdateSnapshot(StateDigest digest, string lastAction)
        {
            try
            {
                var payload = new
                {
                    digest,
                    lastAction,
                    elfActive = TaskRunner.IsActive,
                    timestamp = DateTime.Now.ToString("HH:mm:ss")
                };
                SnapshotJson = JsonConvert.SerializeObject(payload);
            }
            catch
            {
                // Snapshot is best-effort; never break the game loop over it.
            }
        }

        public static void Start(int port, IMonitor monitor)
        {
            Monitor = monitor;
            if (Running)
                return;

            try
            {
                Listener = new HttpListener();
                Listener.Prefixes.Add($"http://localhost:{port}/");
                Listener.Start();
                Running = true;
                Monitor.Log($"OBS overlay server listening at http://localhost:{port}/", LogLevel.Info);
                Task.Run(ListenLoop);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to start overlay server on port {port}: {ex.Message}", LogLevel.Warn);
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
                try
                {
                    ctx = await Listener.GetContextAsync();
                }
                catch
                {
                    break; // listener stopped/disposed
                }

                try
                {
                    string path = ctx.Request.Url?.AbsolutePath ?? "/";
                    if (path == "/data")
                        Write(ctx, SnapshotJson, "application/json");
                    else
                        Write(ctx, Html, "text/html");
                }
                catch (Exception ex)
                {
                    Monitor?.Log($"Overlay request error: {ex.Message}", LogLevel.Trace);
                }
            }
        }

        private static void Write(HttpListenerContext ctx, string body, string contentType)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }

        private const string Html = @"<!DOCTYPE html>
<html><head><meta charset='utf-8'><title>StardewAI Overlay</title>
<style>
  body { margin:0; font-family:'Segoe UI',sans-serif; background:transparent; color:#fff; }
  .panel { background:rgba(20,24,16,0.82); border:2px solid #8fde5d; border-radius:12px;
           padding:14px 18px; width:360px; box-shadow:0 4px 18px rgba(0,0,0,.5); }
  .title { font-weight:700; color:#8fde5d; letter-spacing:1px; margin-bottom:8px; font-size:15px; }
  .row { font-size:13px; margin:2px 0; }
  .k { color:#9ad; }
  .action { margin-top:10px; padding-top:8px; border-top:1px solid #3a4a2a; font-size:14px; }
  .action .lbl { color:#8fde5d; font-weight:700; }
  .elf { color:#7CFC00; font-weight:700; }
</style></head>
<body>
  <div class='panel'>
    <div class='title'>STARDEW&nbsp;AI</div>
    <div id='world'></div>
    <div id='player'></div>
    <div id='farm'></div>
    <div class='action'><span class='lbl'>Last action:</span> <span id='action'>...</span></div>
    <div id='elf'></div>
  </div>
<script>
async function tick(){
  try{
    const r = await fetch('/data',{cache:'no-store'});
    const d = await r.json();
    const dg = d.digest || {};
    const w = dg.World||{}, p = dg.Player||{}, f = dg.Farm||{};
    document.getElementById('world').innerHTML =
      `<div class='row'><span class='k'>Date:</span> ${w.Season||''} ${w.Day||''}, Yr ${w.Year||''} &nbsp; <span class='k'>Wx:</span> ${w.Weather||''} &nbsp; <span class='k'>Time:</span> ${w.TimeOfDay||''}</div>`;
    document.getElementById('player').innerHTML =
      `<div class='row'><span class='k'>${p.Name||'Farmer'}</span> &nbsp; <span class='k'>g:</span> ${p.Money||0} &nbsp; <span class='k'>energy:</span> ${p.Energy||0}/${p.MaxEnergy||0}</div>`;
    document.getElementById('farm').innerHTML =
      `<div class='row'><span class='k'>Plots:</span> ${f.OccupiedPlots||0}/${f.TotalPlots||0} planted</div>`;
    document.getElementById('action').textContent = d.lastAction || '(none)';
    document.getElementById('elf').innerHTML = d.elfActive ? `<div class='row elf'>\u2728 Elf working...</div>` : '';
  }catch(e){ /* server not ready */ }
}
setInterval(tick, 1000); tick();
</script>
</body></html>";
    }
}
