using System;
using System.ComponentModel;
using Newtonsoft.Json;
using Rhino;

namespace ScriptNodePlugin.Tools
{
    /// <summary>
    /// MCP tools for interacting with the main Rhino application.
    /// </summary>
    [McpToolClass]
    public class RhinoAppTool
    {
        private readonly GrasshopperContext _ctx;
        private readonly McpServer _server;

        public RhinoAppTool(GrasshopperContext ctx, McpServer server)
        {
            _ctx = ctx;
            _server = server;
        }

        [McpTool]
        [Description("Read the contents of Rhino's command history window. Extremely useful for checking error outputs, warnings, or prints from Rhino/Python that didn't show up in Grasshopper.")]
        public string GetRhinoCommandHistory()
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                try
                {
                    var text = RhinoApp.CommandHistoryWindowText;
                    return JsonConvert.SerializeObject(new { success = true, history = text }, Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        [McpTool]
        [Description("Clear the contents of Rhino's command history window. Useful before running a command to isolate its specific output.")]
        public string ClearRhinoCommandHistory()
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                try
                {
                    RhinoApp.ClearCommandHistoryWindow();
                    return JsonConvert.SerializeObject(new { success = true }, Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }

        [McpTool]
        [Description("Send a macro or command string directly to the Rhino command line. Similar to typing it and pressing enter.")]
        public string RunRhinoCommand([Description("The command to run, e.g. '_Circle 0,0,0 10'")] string command)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                try
                {
                    bool result = RhinoApp.RunScript(command, true);
                    return JsonConvert.SerializeObject(new { success = true, commandRun = result }, Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                }
            });
        }
    }
}
