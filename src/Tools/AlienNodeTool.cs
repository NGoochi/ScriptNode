using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;

namespace ScriptNodePlugin.Tools
{
    [McpToolClass]
    public class AlienNodeTool
    {
        private readonly GrasshopperContext _ctx;
        private readonly McpServer _server;

        public AlienNodeTool(GrasshopperContext ctx, McpServer server)
        {
            _ctx = ctx;
            _server = server;
        }

        [McpTool]
        [Description("Get full serialised state for an Alien node: manual values, metadata hints, live mode, pending changes, script path.")]
        public string GetNodeState(
            [Description("Component instance GUID")] string component_id)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (!Guid.TryParse(component_id, out var guid))
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });

                if (!_server.RegisteredAlienNodes.TryGetValue(guid, out var node))
                    return JsonConvert.SerializeObject(new { success = false, error = "Alien node not found" });

                var jo = node.BuildNodeStateJson();
                return JsonConvert.SerializeObject(new { success = true, state = jo }, Formatting.Indented);
            });
        }

        [McpTool]
        [Description("Set a manual parameter value on an Alien node by name, then trigger Grasshopper recompute.")]
        public string SetParamValue(
            [Description("Component instance GUID")] string component_id,
            [Description("Input parameter name (matches script header)")] string param_name,
            [Description("Value (number, string, or JSON for complex types)")] string value_json)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (!Guid.TryParse(component_id, out var guid))
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });

                if (!_server.RegisteredAlienNodes.TryGetValue(guid, out var node))
                    return JsonConvert.SerializeObject(new { success = false, error = "Alien node not found" });

                object val;
                try
                {
                    val = JsonConvert.DeserializeObject<object>(value_json);
                }
                catch
                {
                    val = value_json;
                }

                node.SetSingleManualValue(param_name, val, fromLive: false);
                node.ClearPendingAndRecompute();
                return JsonConvert.SerializeObject(new { success = true, param = param_name });
            });
        }
    }
}
