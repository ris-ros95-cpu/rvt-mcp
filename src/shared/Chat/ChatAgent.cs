using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Chat
{
    public enum ToolRisk { Read, Write, Destructive, Code }

    /// <summary>UI callbacks the agent uses (implemented by the dockable pane).</summary>
    public interface IChatHost
    {
        void OnStatus(string text);
        void OnToolCall(string tool, string argsJson, ToolRisk risk);
        void OnToolResult(string tool, string result, bool isError);
        /// <summary>Ask the user to approve a non read-only call. Must return quickly-awaitable task.</summary>
        Task<bool> ConfirmAsync(string tool, string description, string argsJson, ToolRisk risk);
    }

    /// <summary>
    /// Provider-agnostic tool-use loop: Anthropic Messages API or OpenAI Chat Completions.
    /// Tools come from rvt-mcp.exe (tools/list); every call goes back through tools/call.
    /// </summary>
    public sealed class ChatAgent
    {
        public const int MaxRounds = 20;
        public const int OpenAiToolLimit = 128;

        public static readonly string SystemPrompt =
            "You are an assistant embedded in Autodesk Revit via the rvt-mcp tools. " +
            "Inspect the model with read-only tools before changing anything. " +
            "Prefer revit_batch_execute for multi-step edits so they land as one undoable step. " +
            "Every non read-only tool call is shown to the user for approval; if one is declined, do not retry it, explain instead. " +
            "Reply in the user's language (French by default), concisely. Units: follow the tool descriptions.";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private readonly McpStdioClient _mcp;
        private readonly IChatHost _host;
        private JArray _tools;
        private Dictionary<string, JObject> _toolByName = new Dictionary<string, JObject>(StringComparer.Ordinal);
        private readonly List<JObject> _anthropicHistory = new List<JObject>();
        private readonly List<JObject> _openAiHistory = new List<JObject>();
        private readonly HashSet<string> _sessionAllowed = new HashSet<string>(StringComparer.Ordinal);

        public string Provider { get; set; } = "anthropic";   // "anthropic" | "openai"
        public string Model { get; set; } = "claude-sonnet-4-5";

        public ChatAgent(McpStdioClient mcp, IChatHost host) { _mcp = mcp; _host = host; }

        public void Reset()
        {
            _anthropicHistory.Clear();
            _openAiHistory.Clear();
            _sessionAllowed.Clear();
        }

        public void AllowForSession(string tool) { _sessionAllowed.Add(tool); }

        public static ToolRisk Classify(JObject tool)
        {
            var name = tool.Value<string>("name") ?? "";
            if (name.IndexOf("send_code", StringComparison.OrdinalIgnoreCase) >= 0) return ToolRisk.Code;
            var ann = tool["annotations"] as JObject;
            if (ann != null)
            {
                if (ann.Value<bool?>("readOnlyHint") == true) return ToolRisk.Read;
                if (ann.Value<bool?>("destructiveHint") == true) return ToolRisk.Destructive;
                return ToolRisk.Write;
            }
            if (ToolReadPolicy.ReadOnlyCommands.Contains(name.StartsWith("revit_") ? name.Substring(6) : name)) return ToolRisk.Read;
            return ToolRisk.Destructive; // unknown => strictest confirmation
        }

        private async Task EnsureToolsAsync(CancellationToken ct)
        {
            if (!_mcp.IsRunning) { _tools = null; await _mcp.StartAsync(ct).ConfigureAwait(false); }
            if (_tools != null) return;
            _tools = await _mcp.ListToolsAsync(ct).ConfigureAwait(false);
            _toolByName = _tools.OfType<JObject>().Where(t => t["name"] != null)
                .ToDictionary(t => t.Value<string>("name"), t => t, StringComparer.Ordinal);
        }

        public async Task<string> SendAsync(string userText, string apiKey, CancellationToken ct)
        {
            await EnsureToolsAsync(ct).ConfigureAwait(false);
            return Provider == "openai"
                ? await RunOpenAiAsync(userText, apiKey, ct).ConfigureAwait(false)
                : await RunAnthropicAsync(userText, apiKey, ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ tools
        private async Task<Tuple<string, bool>> ExecuteToolAsync(string name, JObject args, CancellationToken ct)
        {
            JObject tool;
            if (!_toolByName.TryGetValue(name, out tool))
                return Tuple.Create("Unknown tool: " + name, true);
            var risk = Classify(tool);
            var argsJson = args.ToString(Formatting.Indented);
            _host.OnToolCall(name, argsJson, risk);

            if (risk != ToolRisk.Read && !(risk == ToolRisk.Write && _sessionAllowed.Contains(name)))
            {
                bool ok = await _host.ConfirmAsync(name, tool.Value<string>("description") ?? "", argsJson, risk).ConfigureAwait(false);
                if (!ok)
                {
                    _host.OnToolResult(name, "Refusé par l'utilisateur / declined by user", true);
                    return Tuple.Create("The user DECLINED this tool call. Do not retry it; explain or ask what to change.", true);
                }
            }
            try
            {
                var r = await _mcp.CallToolAsync(name, args, ct).ConfigureAwait(false);
                var text = r.Item1.Length > 60000 ? r.Item1.Substring(0, 60000) + "\n…[truncated]" : r.Item1;
                _host.OnToolResult(name, text, r.Item2);
                return Tuple.Create(text, r.Item2);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _host.OnToolResult(name, ex.Message, true);
                return Tuple.Create("Tool error: " + ex.Message, true);
            }
        }

        private static JObject SchemaOf(JObject tool)
        {
            var s = tool["inputSchema"] as JObject;
            return s != null ? (JObject)s.DeepClone() : new JObject { ["type"] = "object", ["properties"] = new JObject() };
        }

        // -------------------------------------------------------------- anthropic
        private async Task<string> RunAnthropicAsync(string userText, string apiKey, CancellationToken ct)
        {
            var tools = new JArray(_tools.OfType<JObject>().Select(t => new JObject
            {
                ["name"] = t["name"],
                ["description"] = Truncate(t.Value<string>("description"), 1000),
                ["input_schema"] = SchemaOf(t)
            }));
            _anthropicHistory.Add(new JObject { ["role"] = "user", ["content"] = userText });

            for (int round = 0; round < MaxRounds; round++)
            {
                _host.OnStatus("Claude réfléchit… (" + (round + 1) + ")");
                var body = new JObject
                {
                    ["model"] = Model,
                    ["max_tokens"] = 8000,
                    ["system"] = SystemPrompt,
                    ["tools"] = tools,
                    ["messages"] = new JArray(_anthropicHistory)
                };
                var json = body.ToString(Formatting.None);
                var resp = await PostAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("x-api-key", apiKey);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    return req;
                }, ct).ConfigureAwait(false);

                var content = (JArray)resp["content"] ?? new JArray();
                _anthropicHistory.Add(new JObject { ["role"] = "assistant", ["content"] = content });
                var uses = content.OfType<JObject>().Where(b => b.Value<string>("type") == "tool_use").ToList();
                var text = string.Join("\n", content.OfType<JObject>().Where(b => b.Value<string>("type") == "text").Select(b => b.Value<string>("text")));
                if (uses.Count == 0) return text;

                var results = new JArray();
                foreach (var u in uses)
                {
                    var r = await ExecuteToolAsync(u.Value<string>("name"), u["input"] as JObject ?? new JObject(), ct).ConfigureAwait(false);
                    results.Add(new JObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = u["id"],
                        ["content"] = r.Item1,
                        ["is_error"] = r.Item2
                    });
                }
                _anthropicHistory.Add(new JObject { ["role"] = "user", ["content"] = results });
            }
            return "Limite de " + MaxRounds + " étapes atteinte. / Step limit reached.";
        }

        // ----------------------------------------------------------------- openai
        private async Task<string> RunOpenAiAsync(string userText, string apiKey, CancellationToken ct)
        {
            // OpenAI caps a request at 128 tools: keep read-only tools first, then the rest.
            var ordered = _tools.OfType<JObject>().OrderBy(t => Classify(t) == ToolRisk.Read ? 0 : 1).Take(OpenAiToolLimit);
            var tools = new JArray(ordered.Select(t => new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = t["name"],
                    ["description"] = Truncate(t.Value<string>("description"), 1000),
                    ["parameters"] = SchemaOf(t)
                }
            }));
            if (_openAiHistory.Count == 0) _openAiHistory.Add(new JObject { ["role"] = "system", ["content"] = SystemPrompt });
            _openAiHistory.Add(new JObject { ["role"] = "user", ["content"] = userText });

            for (int round = 0; round < MaxRounds; round++)
            {
                _host.OnStatus("OpenAI réfléchit… (" + (round + 1) + ")");
                var body = new JObject { ["model"] = Model, ["messages"] = new JArray(_openAiHistory), ["tools"] = tools };
                var json = body.ToString(Formatting.None);
                var resp = await PostAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("Authorization", "Bearer " + apiKey);
                    return req;
                }, ct).ConfigureAwait(false);

                var msg = (JObject)resp["choices"]?[0]?["message"] ?? new JObject();
                _openAiHistory.Add(msg);
                var calls = msg["tool_calls"] as JArray;
                if (calls == null || calls.Count == 0) return msg.Value<string>("content") ?? "";

                foreach (var c in calls.OfType<JObject>())
                {
                    var fn = c["function"] as JObject ?? new JObject();
                    JObject args;
                    try { args = JObject.Parse(fn.Value<string>("arguments") ?? "{}"); } catch { args = new JObject(); }
                    var r = await ExecuteToolAsync(fn.Value<string>("name"), args, ct).ConfigureAwait(false);
                    _openAiHistory.Add(new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = c["id"],
                        ["content"] = (r.Item2 ? "ERROR: " : "") + r.Item1
                    });
                }
            }
            return "Limite de " + MaxRounds + " étapes atteinte. / Step limit reached.";
        }

        // ------------------------------------------------------------------ http
        private static async Task<JObject> PostAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                using (var req = makeRequest())
                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    int code = (int)resp.StatusCode;
                    if ((code == 429 || code == 529 || code >= 500) && attempt < 2)
                    {
                        await Task.Delay(2000 * (attempt + 1), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        string detail = text;
                        try { detail = JObject.Parse(text)["error"]?["message"]?.ToString() ?? text; } catch { }
                        throw new InvalidOperationException("API " + code + ": " + Truncate(detail, 500));
                    }
                    return JObject.Parse(text);
                }
            }
        }

        private static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
