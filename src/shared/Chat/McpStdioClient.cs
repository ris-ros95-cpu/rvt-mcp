using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Chat
{
    /// <summary>
    /// Minimal MCP client (JSON-RPC 2.0 over stdio) that spawns the same rvt-mcp.exe that
    /// Claude/Codex use. The chat pane therefore sees exactly the same tool surface, schemas,
    /// annotations and safety gates (read-only mode, send_code gate, batch_execute undo) as
    /// external clients. The server is pinned to this Revit process with --target pid:&lt;pid&gt;.
    /// </summary>
    public sealed class McpStdioClient : IDisposable
    {
        private Process _proc;
        private int _nextId;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending =
            new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public static string ResolveServerPath()
        {
            var env = Environment.GetEnvironmentVariable("RVTMCP_SERVER_PATH");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(la, "Bimwright", "rvt-mcp", "server", "current", "rvt-mcp.exe"),
                Path.Combine(la, "Bimwright", "rvt-mcp", "server", "current", "RvtMcp.Server.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        public bool IsRunning { get { return _proc != null && !_proc.HasExited; } }

        public async Task StartAsync(CancellationToken ct)
        {
            if (IsRunning) return;
            var exe = ResolveServerPath();
            if (exe == null)
                throw new FileNotFoundException("rvt-mcp.exe introuvable / not found. Lancez install.ps1 (ou definissez RVTMCP_SERVER_PATH).");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--target pid:" + Process.GetCurrentProcess().Id,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            _proc = Process.Start(psi);
            _proc.ErrorDataReceived += (s, e) => { };
            _proc.BeginErrorReadLine();
            var reader = _proc.StandardOutput;
            var _ = Task.Run(() => ReadLoop(reader));

            await RequestAsync("initialize", new JObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "rvt-mcp-revit-chat", ["version"] = "1.0" }
            }, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await NotifyAsync("notifications/initialized").ConfigureAwait(false);
        }

        public async Task<JArray> ListToolsAsync(CancellationToken ct)
        {
            var all = new JArray();
            string cursor = null;
            do
            {
                var p = new JObject();
                if (cursor != null) p["cursor"] = cursor;
                var res = await RequestAsync("tools/list", p, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                foreach (var t in (JArray)res["tools"] ?? new JArray()) all.Add(t);
                cursor = res.Value<string>("nextCursor");
            } while (!string.IsNullOrEmpty(cursor));
            return all;
        }

        /// <summary>Calls a tool and returns the concatenated text content (and isError).</summary>
        public async Task<Tuple<string, bool>> CallToolAsync(string name, JObject args, CancellationToken ct)
        {
            var res = await RequestAsync("tools/call", new JObject { ["name"] = name, ["arguments"] = args ?? new JObject() },
                ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            var sb = new StringBuilder();
            foreach (var c in (JArray)res["content"] ?? new JArray())
            {
                if (c.Value<string>("type") == "text") sb.AppendLine(c.Value<string>("text"));
                else sb.AppendLine("[" + c.Value<string>("type") + " content omitted]");
            }
            return Tuple.Create(sb.ToString().Trim(), res.Value<bool?>("isError") ?? false);
        }

        private async Task<JObject> RequestAsync(string method, JObject p, CancellationToken ct, TimeSpan timeout)
        {
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await WriteAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p }).ConfigureAwait(false);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout);
                using (cts.Token.Register(() => tcs.TrySetCanceled()))
                {
                    var msg = await tcs.Task.ConfigureAwait(false);
                    if (msg["error"] != null) throw new InvalidOperationException("MCP " + method + ": " + msg["error"]["message"]);
                    return (JObject)msg["result"] ?? new JObject();
                }
            }
        }

        private Task NotifyAsync(string method)
        {
            return WriteAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = method });
        }

        private async Task WriteAsync(JObject msg)
        {
            if (!IsRunning) throw new InvalidOperationException("rvt-mcp server is not running.");
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _proc.StandardInput.WriteLineAsync(msg.ToString(Formatting.None)).ConfigureAwait(false);
                await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        private async Task ReadLoop(StreamReader reader)
        {
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length == 0 || line[0] != '{') continue;
                    JObject msg;
                    try { msg = JObject.Parse(line); } catch { continue; }
                    var idTok = msg["id"];
                    if (idTok == null || msg["method"] != null) continue; // server notifications/requests ignored
                    TaskCompletionSource<JObject> tcs;
                    if (_pending.TryRemove(idTok.Value<int>(), out tcs)) tcs.TrySetResult(msg);
                }
            }
            catch { }
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException("rvt-mcp server exited."));
            _pending.Clear();
        }

        public void Dispose()
        {
            try { if (IsRunning) { _proc.StandardInput.Close(); if (!_proc.WaitForExit(2000)) _proc.Kill(); } } catch { }
            _proc = null;
        }
    }
}
