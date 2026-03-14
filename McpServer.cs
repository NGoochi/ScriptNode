using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
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
    /// Auto-starts when the first ScriptNodeComponent is placed on the canvas.
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

        // ── Registered ScriptNode instances ───────────────────────────
        private readonly ConcurrentDictionary<Guid, ScriptNodeComponent> _nodes =
            new ConcurrentDictionary<Guid, ScriptNodeComponent>();

        public IReadOnlyDictionary<Guid, ScriptNodeComponent> RegisteredNodes => _nodes;

        public void RegisterNode(ScriptNodeComponent node)
        {
            _nodes[node.InstanceGuid] = node;
        }

        public void UnregisterNode(ScriptNodeComponent node)
        {
            _nodes.TryRemove(node.InstanceGuid, out _);
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
                RhinoApp.WriteLine($"[ScriptNode MCP] Server started on http://127.0.0.1:{_port}/mcp  ({_tools.Count} tools)");
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
                RhinoApp.WriteLine("[ScriptNode MCP] Server stopped");
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
                    resp.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
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
                        server = "ScriptNode MCP",
                        port = _port,
                        toolCount = _tools.Count,
                        activeScriptNodes = _nodes.Count
                    }, origin);
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

                var responseJson = JsonSerializer.Serialize(responseObj, new JsonSerializerOptions
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
                        serverInfo = new { name = "ScriptNode MCP", version = "0.1.0" },
                        instructions = "ScriptNode MCP: Control Grasshopper ScriptNode components.\n\nTools:\n- get_canvas_info: list all components, connections, and status\n- get_scriptnode_info: deep-dive into a ScriptNode (header, params, errors)\n- get_script_source: read the Python script file\n- write_script_source: write code to the Python script (triggers auto-reload)\n- get_error_log: read the gh_errors.log file\n- get_component_outputs: read output values from any component"
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
            resp.ContentType = "application/json";
            if (origin != null) resp.Headers.Add("Access-Control-Allow-Origin", origin);
            var json = JsonSerializer.Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            resp.Close();
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
