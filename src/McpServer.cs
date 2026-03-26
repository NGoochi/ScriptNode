using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;

namespace ScriptNodePlugin
{
    // ── Marker attributes for tool discovery ──────────────────────────
    [AttributeUsage(AttributeTargets.Class)]
    public class McpToolClassAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class McpToolAttribute : Attribute { }

    // ── MCP Server ───────────────────────────────────────────────────
    /// <summary>
    /// Singleton MCP server using HttpListener with Streamable HTTP transport.
    /// Auto-starts when the first Alien node is placed on the canvas.
    /// </summary>
    public class McpServer : IDisposable
    {
        private const int DEFAULT_PORT = 9876;

        // ── Singleton ────────────────────────────────────────────────
        private static readonly Lazy<McpServer> _instance =
            new Lazy<McpServer>(() => new McpServer());
        public static McpServer Instance => _instance.Value;

        // ── State ────────────────────────────────────────────────────
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _listenerTask;
        private int _port;
        private GrasshopperContext _context;
        private readonly List<ToolInfo> _tools = new List<ToolInfo>();
        private bool _disposed;

        public bool IsRunning { get; private set; }
        public int Port => _port;

        // ── Registered Alien nodes ─────────────────────────────────────
        private readonly ConcurrentDictionary<Guid, AlienNodeComponent> _alienNodes =
            new ConcurrentDictionary<Guid, AlienNodeComponent>();

        public IReadOnlyDictionary<Guid, AlienNodeComponent> RegisteredAlienNodes => _alienNodes;
        public int RegisteredAlienNodeCount => _alienNodes.Count;

        public void RegisterAlienNode(AlienNodeComponent node)
        {
            _alienNodes[node.InstanceGuid] = node;
        }

        public void UnregisterAlienNode(AlienNodeComponent node)
        {
            _alienNodes.TryRemove(node.InstanceGuid, out _);
        }

        /// <summary>MCP tools still refer to "registered script nodes" — same as Alien.</summary>
        public IReadOnlyDictionary<Guid, AlienNodeComponent> RegisteredNodes => _alienNodes;

        public void RegisterNode(AlienNodeComponent node) => RegisterAlienNode(node);

        public void UnregisterNode(AlienNodeComponent node) => UnregisterAlienNode(node);

        // ── Registered legacy DataNode instances ─────────────────────────
        private readonly ConcurrentDictionary<Guid, LegacyDataNode> _dataNodes =
            new ConcurrentDictionary<Guid, LegacyDataNode>();

        public IReadOnlyDictionary<Guid, LegacyDataNode> RegisteredDataNodes => _dataNodes;

        public void RegisterDataNode(LegacyDataNode node)
        {
            _dataNodes[node.InstanceGuid] = node;
        }

        public void UnregisterDataNode(LegacyDataNode node)
        {
            _dataNodes.TryRemove(node.InstanceGuid, out _);
        }

        private readonly AlienNodeWebSocketHub _wsHub = new AlienNodeWebSocketHub();

        public void BroadcastAlienNodeState(AlienNodeComponent node)
        {
            if (node == null) return;
            try
            {
                var json = node.BuildNodeStateJson().ToString(Formatting.None);
                _wsHub.BroadcastText(node.InstanceGuid, json);
            }
            catch { }
        }

        // ── Lifecycle ────────────────────────────────────────────────
        public void Start(int port = DEFAULT_PORT)
        {
            if (IsRunning) return;

            _port = port;
            _cts = new CancellationTokenSource();

            try
            {
                _context = new GrasshopperContext();
                DiscoverTools();

                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();

                _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);

                IsRunning = true;
                RhinoApp.WriteLine($"[Alien MCP] Server started on http://127.0.0.1:{_port}/mcp  ({_tools.Count} tools)");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[ScriptNode MCP] Failed to start: {ex.Message}");
                _cts?.Dispose();
                _cts = null;
                _listener?.Close();
                _listener = null;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();
                try { _listenerTask?.Wait(TimeSpan.FromSeconds(2)); }
                catch (AggregateException) { }
            }
            catch { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _listener = null;
                _listenerTask = null;
                _context = null;
                IsRunning = false;
                RhinoApp.WriteLine("[Alien MCP] Server stopped");
            }
        }

        // ── Tool Discovery ───────────────────────────────────────────
        private void DiscoverTools()
        {
            _tools.Clear();
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var type in assembly.GetTypes())
            {
                if (type.GetCustomAttribute<McpToolClassAttribute>() == null) continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var toolAttr = method.GetCustomAttribute<McpToolAttribute>();
                    if (toolAttr == null) continue;

                    var descAttr = method.GetCustomAttribute<DescriptionAttribute>();

                    var parameters = new List<ToolParameter>();
                    foreach (var p in method.GetParameters())
                    {
                        var pdesc = p.GetCustomAttribute<DescriptionAttribute>();
                        parameters.Add(new ToolParameter
                        {
                            Name = p.Name,
                            Description = pdesc?.Description ?? "",
                            Type = GetJsonType(p.ParameterType),
                            IsRequired = !p.HasDefaultValue
                        });
                    }

                    _tools.Add(new ToolInfo
                    {
                        Name = ToSnakeCase(method.Name),
                        Description = descAttr?.Description ?? "",
                        DeclaringType = type,
                        Method = method,
                        Parameters = parameters
                    });
                }
            }
        }

        // ── HTTP Loop ────────────────────────────────────────────────
        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(ctx, ct), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            try
            {
                var path = req.Url?.AbsolutePath ?? "/";

                // Origin validation
                var origin = ValidateOrigin(req, resp);
                if (origin == "BLOCKED") return;

                // CORS preflight
                if (req.HttpMethod == "OPTIONS")
                {
                    resp.Headers.Add("Access-Control-Allow-Origin", origin ?? "*");
                    resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");
                    resp.StatusCode = 204;
                    resp.Close();
                    return;
                }

                if (path == "/mcp" && req.HttpMethod == "POST")
                {
                    await HandleMcpPostAsync(ctx, origin);
                    return;
                }

                if (path == "/" || path == "/health")
                {
                    await WriteJsonResponse(resp, 200, new
                    {
                        status = "ok",
                        server = "Alien MCP",
                        port = _port,
                        toolCount = _tools.Count,
                        activeAlienNodes = _alienNodes.Count,
                        activeDataNodes = _dataNodes.Count
                    }, origin);
                    return;
                }

                // WebSocket upgrade for Alien editor
                if (req.HttpMethod == "GET"
                    && path.StartsWith("/ws/node/", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(req.Headers["Upgrade"], "websocket", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Run(() => HandleAlienWebSocketAsync(ctx, ct), ct);
                    return;
                }

                // Dashboard (multi-node tabbed page)
                if (req.HttpMethod == "GET" && (path == "/editor" || path == "/editor/"))
                {
                    var html = EditorHtml.GetDashboard(_port);
                    resp.Headers.Add("Access-Control-Allow-Origin", origin ?? "*");
                    resp.ContentType = "text/html; charset=utf-8";
                    resp.StatusCode = 200;
                    var bytes = Encoding.UTF8.GetBytes(html);
                    await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    resp.Close();
                    return;
                }

                // Browser editor HTML (single node)
                if (req.HttpMethod == "GET" && path.StartsWith("/editor/node/", StringComparison.OrdinalIgnoreCase))
                {
                    var seg = path.Substring("/editor/node/".Length).Trim('/');
                    if (Guid.TryParse(seg, out var eguid))
                    {
                        var html = EditorHtml.GetPage(_port, eguid);
                        resp.Headers.Add("Access-Control-Allow-Origin", origin ?? "*");
                        resp.ContentType = "text/html; charset=utf-8";
                        resp.StatusCode = 200;
                        var bytes = Encoding.UTF8.GetBytes(html);
                        await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        resp.Close();
                        return;
                    }
                }

                // API: list all registered Alien nodes
                if (req.HttpMethod == "GET" && (path == "/api/nodes" || path == "/api/nodes/"))
                {
                    var nodes = new JArray();
                    foreach (var kv in _alienNodes)
                    {
                        var n = kv.Value;
                        nodes.Add(new JObject
                        {
                            ["guid"] = kv.Key.ToString(),
                            ["scriptName"] = !string.IsNullOrEmpty(n.ScriptPath)
                                ? System.IO.Path.GetFileNameWithoutExtension(n.ScriptPath) : "ALIEN",
                            ["scriptPath"] = n.ScriptPath ?? "",
                            ["paramCount"] = Math.Max(0, n.Params.Input.Count - 1),
                        });
                    }
                    await WriteJsonResponse(resp, 200, new JObject { ["success"] = true, ["nodes"] = nodes }, origin);
                    return;
                }

                // REST: GET state
                if (req.HttpMethod == "GET" && path.StartsWith("/api/node/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/state", StringComparison.OrdinalIgnoreCase))
                {
                    var seg = path.Substring("/api/node/".Length);
                    seg = seg.Substring(0, seg.Length - "/state".Length).Trim('/');
                    if (Guid.TryParse(seg, out var sguid) && _alienNodes.TryGetValue(sguid, out var snode))
                    {
                        var jo = snode.BuildNodeStateJson();
                        await WriteJsonResponse(resp, 200, new JObject { ["success"] = true, ["state"] = jo }, origin);
                        return;
                    }
                    await WriteJsonResponse(resp, 404, new { success = false, error = "node not found" }, origin);
                    return;
                }

                // REST: POST batch values
                if (req.HttpMethod == "POST" && path.StartsWith("/api/node/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/values", StringComparison.OrdinalIgnoreCase))
                {
                    var seg = path.Substring("/api/node/".Length);
                    seg = seg.Substring(0, seg.Length - "/values".Length).Trim('/');
                    if (Guid.TryParse(seg, out var vguid) && _alienNodes.TryGetValue(vguid, out var vnode))
                    {
                        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var root = JObject.Parse(body);
                        var valuesObj = root["values"] as JObject;
                        if (valuesObj != null)
                        {
                            var dict = new Dictionary<string, object>();
                            foreach (var p in valuesObj.Properties())
                                dict[p.Name] = p.Value.ToObject<object>();
                            _context.ExecuteOnUiThread(() =>
                            {
                                vnode.ApplyManualValuesFromDictionary(dict, fromLive: false);
                                vnode.ClearPendingAndRecompute();
                            });
                        }
                        await WriteJsonResponse(resp, 200, new { success = true }, origin);
                        return;
                    }
                    await WriteJsonResponse(resp, 404, new { success = false, error = "node not found" }, origin);
                    return;
                }

                resp.StatusCode = 404;
                resp.Close();
            }
            catch
            {
                try { resp.StatusCode = 500; resp.Close(); } catch { }
            }
        }

        // ── JSON-RPC dispatch ────────────────────────────────────────
        private async Task HandleMcpPostAsync(HttpListenerContext ctx, string origin)
        {
            var resp = ctx.Response;
            try
            {
                resp.Headers.Add("Access-Control-Allow-Origin", origin ?? "*");

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync();

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
                var hasId = root.TryGetProperty("id", out var id);
                var paramsEl = root.TryGetProperty("params", out var p) ? p : default;

                bool isNotification = !hasId || id.ValueKind == JsonValueKind.Undefined;

                object result = null;
                string errorMsg = null;
                int errorCode = 0;

                try { result = await DispatchAsync(method, paramsEl); }
                catch (Exception ex) { errorMsg = ex.Message; errorCode = -32603; }

                if (isNotification)
                {
                    resp.StatusCode = 202;
                    resp.Close();
                    return;
                }

                var responseObj = new Dictionary<string, object> { ["jsonrpc"] = "2.0" };
                if (id.ValueKind == JsonValueKind.Number) responseObj["id"] = id.GetInt32();
                else if (id.ValueKind == JsonValueKind.String) responseObj["id"] = id.GetString();

                if (errorMsg != null)
                    responseObj["error"] = new Dictionary<string, object> { ["code"] = errorCode, ["message"] = errorMsg };
                else
                    responseObj["result"] = result;

                var responseJson = System.Text.Json.JsonSerializer.Serialize(responseObj, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                resp.ContentType = "application/json";
                resp.StatusCode = 200;
                var bytes = Encoding.UTF8.GetBytes(responseJson);
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await resp.OutputStream.FlushAsync();
            }
            catch { resp.StatusCode = 400; }
            finally { resp.Close(); }
        }

        private async Task<object> DispatchAsync(string method, JsonElement paramsEl)
        {
            switch (method)
            {
                case "initialize":
                    return new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "Alien MCP", version = "0.2.0" },
                        instructions = "Alien MCP: Grasshopper Alien nodes (Python + manual values) and legacy DataNode.\n\nTools:\n- get_canvas_info, get_component_outputs\n- get_scriptnode_info, get_script_source, write_script_source (requires confirm_overwrite:true to replace non-empty files; backup .bak created), get_error_log\n- get_node_state, set_param_value\n- get_datanode_info, set_datanode_values, add_datanode_items, set_datanode_schema\n- get_rhino_command_history, clear_rhino_command_history, run_rhino_command"
                    };

                case "initialized":
                case "notifications/initialized":
                    return new { };

                case "tools/list":
                    return HandleToolsList();

                case "tools/call":
                    return await HandleToolCallAsync(paramsEl);

                case "ping":
                    return new { };

                default:
                    throw new Exception($"Unknown method: {method}");
            }
        }

        private object HandleToolsList()
        {
            var tools = _tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                inputSchema = new
                {
                    type = "object",
                    properties = t.Parameters.ToDictionary(
                        p => p.Name,
                        p => new { type = p.Type, description = p.Description }),
                    required = t.Parameters.Where(p => p.IsRequired).Select(p => p.Name).ToArray()
                }
            }).ToList();
            return new { tools };
        }

        private async Task<object> HandleToolCallAsync(JsonElement paramsEl)
        {
            var name = paramsEl.GetProperty("name").GetString();
            var arguments = paramsEl.TryGetProperty("arguments", out var a) ? a : default;

            var tool = _tools.FirstOrDefault(t => t.Name == name);
            if (tool == null) throw new Exception($"Unknown tool: {name}");

            // Create tool instance, passing context + server
            object instance;
            var ctors = tool.DeclaringType.GetConstructors();
            var ctor = ctors.FirstOrDefault(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 2
                    && ps[0].ParameterType == typeof(GrasshopperContext)
                    && ps[1].ParameterType == typeof(McpServer);
            });

            if (ctor != null)
                instance = ctor.Invoke(new object[] { _context, this });
            else
                instance = Activator.CreateInstance(tool.DeclaringType, _context);

            // Build method arguments
            var methodParams = tool.Method.GetParameters();
            var args = new object[methodParams.Length];
            for (int i = 0; i < methodParams.Length; i++)
            {
                var param = methodParams[i];
                if (arguments.ValueKind == JsonValueKind.Object &&
                    arguments.TryGetProperty(param.Name, out var argVal))
                {
                    args[i] = ConvertJsonValue(argVal, param.ParameterType);
                }
                else if (param.HasDefaultValue)
                {
                    args[i] = param.DefaultValue;
                }
                else
                {
                    throw new Exception($"Missing required parameter: {param.Name}");
                }
            }

            var result = tool.Method.Invoke(instance, args);

            if (result is Task task)
            {
                await task;
                var resProp = task.GetType().GetProperty("Result");
                result = resProp?.GetValue(task);
            }

            return new
            {
                content = new[] { new { type = "text", text = result?.ToString() ?? "" } },
                isError = false
            };
        }

        // ── Helpers ──────────────────────────────────────────────────
        private string ValidateOrigin(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                try
                {
                    var uri = new Uri(origin);
                    if (uri.Host != "127.0.0.1" && uri.Host != "localhost")
                    {
                        resp.StatusCode = 403;
                        resp.Close();
                        return "BLOCKED";
                    }
                    return origin;
                }
                catch
                {
                    resp.StatusCode = 403;
                    resp.Close();
                    return "BLOCKED";
                }
            }
            return null;
        }

        private static async Task WriteJsonResponse(HttpListenerResponse resp, int status, object data, string origin)
        {
            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            if (origin != null) resp.Headers.Add("Access-Control-Allow-Origin", origin);
            string json;
            if (data is JToken jt)
                json = jt.ToString(Formatting.None);
            else
                json = System.Text.Json.JsonSerializer.Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            resp.Close();
        }

        private async Task HandleAlienWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var prefix = "/ws/node/";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { try { ctx.Response.Close(); } catch { } return; }
            var gstr = path.Substring(prefix.Length).Trim('/');
            if (!Guid.TryParse(gstr, out var guid)) { try { ctx.Response.Close(); } catch { } return; }

            WebSocket ws;
            try
            {
                ws = (await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
                return;
            }
            IDisposable reg = null;
            try
            {
                reg = _wsHub.Register(guid, ws);

                _context.ExecuteOnUiThread(() =>
                {
                    if (_alienNodes.TryGetValue(guid, out var node))
                        BroadcastAlienNodeState(node);
                });

                var buffer = new byte[64 * 1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult rr;
                    try
                    {
                        rr = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }

                    if (rr.MessageType == WebSocketMessageType.Close)
                        break;

                    if (rr.MessageType != WebSocketMessageType.Text) continue;

                    var json = Encoding.UTF8.GetString(buffer, 0, rr.Count);
                    JObject msg;
                    try { msg = JObject.Parse(json); } catch { continue; }
                    var type = msg.Value<string>("type");
                    if (type == null) continue;

                    if (!_alienNodes.TryGetValue(guid, out var node)) continue;

                    switch (type)
                    {
                        case "setLive":
                            {
                                var en = msg.Value<bool?>("enabled") ?? false;
                                _context.ExecuteOnUiThread(() =>
                                {
                                    node.SetLiveMode(en);
                                    BroadcastAlienNodeState(node);
                                });
                                break;
                            }
                        case "apply":
                            {
                                var values = msg["values"] as JObject;
                                if (values == null) break;
                                var dict = new Dictionary<string, object>();
                                foreach (var p in values.Properties())
                                    dict[p.Name] = p.Value?.ToObject<object>();
                                _context.ExecuteOnUiThread(() =>
                                {
                                    node.ApplyManualValuesFromDictionary(dict, fromLive: false);
                                    node.ClearPendingAndRecompute();
                                });
                                break;
                            }
                        case "update":
                            {
                                var pname = msg.Value<string>("param");
                                var valTok = msg["value"];
                                if (string.IsNullOrEmpty(pname)) break;

                                object val = valTok?.ToObject<object>();
                                _context.ExecuteOnUiThread(() =>
                                {
                                    node.SetSingleManualValue(pname, val, fromLive: node.LiveMode);
                                });
                                break;
                            }
                        case "store":
                            {
                                var pname = msg.Value<string>("param");
                                var valTok = msg["value"];
                                if (string.IsNullOrEmpty(pname)) break;
                                object val = valTok?.ToObject<object>();
                                _context.ExecuteOnUiThread(() =>
                                {
                                    node.StoreManualValueOnly(pname, val);
                                });
                                break;
                            }
                    }
                }
            }
            finally
            {
                reg?.Dispose();
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
        }

        private static object ConvertJsonValue(JsonElement el, Type target)
        {
            if (target == typeof(string)) return el.GetString();
            if (target == typeof(int)) return el.GetInt32();
            if (target == typeof(bool)) return el.GetBoolean();
            if (target == typeof(double)) return el.GetDouble();
            return el.GetString();
        }

        private static string GetJsonType(Type type)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float))
                return "number";
            if (type == typeof(bool)) return "boolean";
            return "string";
        }

        private static string ToSnakeCase(string name)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
                sb.Append(char.ToLower(name[i]));
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        // ── Inner types ──────────────────────────────────────────────
        private class ToolInfo
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public Type DeclaringType { get; set; }
            public MethodInfo Method { get; set; }
            public List<ToolParameter> Parameters { get; set; }
        }

        private class ToolParameter
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string Type { get; set; }
            public bool IsRequired { get; set; }
        }
    }
}
