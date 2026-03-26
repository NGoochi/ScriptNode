using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Grasshopper.Kernel;
using Newtonsoft.Json;

namespace ScriptNodePlugin.Tools
{
    /// <summary>
    /// MCP tools for interacting with ScriptNode components.
    /// </summary>
    [McpToolClass]
    public class ScriptNodeTool
    {
        private readonly GrasshopperContext _ctx;
        private readonly McpServer _server;

        public ScriptNodeTool(GrasshopperContext ctx, McpServer server)
        {
            _ctx = ctx;
            _server = server;
        }

        [McpTool]
        [Description("Get detailed information about a ScriptNode component: script path, parsed header (inputs/outputs with types), current runtime messages, and file watcher status. If no component_id is provided, returns info for all registered ScriptNodes.")]
        public string GetScriptnodeInfo(
            [Description("Optional GUID of a specific ScriptNode. Omit to get info for all ScriptNodes.")] string component_id = null)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (component_id != null)
                {
                    // Single node lookup
                    if (!Guid.TryParse(component_id, out var guid))
                        return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });

                    if (!_server.RegisteredNodes.TryGetValue(guid, out var node))
                        return JsonConvert.SerializeObject(new { success = false, error = $"ScriptNode not found: {component_id}" });

                    return JsonConvert.SerializeObject(new { success = true, node = BuildAlienNodeInfo(node) }, Formatting.Indented);
                }

                // All nodes
                var nodes = _server.RegisteredNodes.Values
                    .Select(n => BuildAlienNodeInfo(n))
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = nodes.Count,
                    nodes
                }, Formatting.Indented);
            });
        }

        [McpTool]
        [Description("Read the full contents of the Python script file that a ScriptNode is pointing at. Returns the .py source code.")]
        public string GetScriptSource(
            [Description("GUID of the ScriptNode component")] string component_id)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (!Guid.TryParse(component_id, out var guid))
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });

                if (!_server.RegisteredNodes.TryGetValue(guid, out var node))
                    return JsonConvert.SerializeObject(new { success = false, error = $"ScriptNode not found: {component_id}" });

                var path = node.ScriptPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return JsonConvert.SerializeObject(new { success = false, error = $"Script file not found: {path}" });

                var content = File.ReadAllText(path);
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    path,
                    lineCount = content.Split('\n').Length,
                    content
                }, Formatting.Indented);
            });
        }

        [McpTool]
        [Description("Write new content to the Python script file that a ScriptNode is pointing at. The ScriptNode's FileSystemWatcher will detect the change and auto-reload the script, updating parameters and re-executing. If the file already exists and is not empty, you must pass confirm_overwrite: true or the call is rejected (prevents accidental wipes). A timestamped .bak copy is saved before overwriting non-empty files.")]
        public string WriteScriptSource(
            [Description("GUID of the ScriptNode component")] string component_id,
            [Description("The full Python source code to write to the file")] string content,
            [Description("Must be true when replacing an existing non-empty file. Omit or false for new/empty files only.")] bool confirm_overwrite = false)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (!Guid.TryParse(component_id, out var guid))
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });

                if (!_server.RegisteredNodes.TryGetValue(guid, out var node))
                    return JsonConvert.SerializeObject(new { success = false, error = $"ScriptNode not found: {component_id}" });

                var path = node.ScriptPath;
                if (string.IsNullOrEmpty(path))
                    return JsonConvert.SerializeObject(new { success = false, error = "ScriptNode has no script_path set" });

                try
                {
                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    string backupPath = null;
                    if (File.Exists(path))
                    {
                        var len = new FileInfo(path).Length;
                        if (len > 0)
                        {
                            if (!confirm_overwrite)
                            {
                                return JsonConvert.SerializeObject(new
                                {
                                    success = false,
                                    error = "File exists and is not empty. Use get_script_source first. To replace it, call write_script_source again with confirm_overwrite: true.",
                                    requires_confirm_overwrite = true,
                                    path,
                                    existing_bytes = len
                                });
                            }

                            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
                            backupPath = path + "." + stamp + ".bak";
                            File.Copy(path, backupPath, overwrite: false);
                        }
                    }

                    File.WriteAllText(path, content);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        path,
                        bytesWritten = content?.Length ?? 0,
                        backup_path = backupPath,
                        message = backupPath != null
                            ? "Previous contents saved to backup_path. Script written; ScriptNode will auto-reload via FileSystemWatcher."
                            : "Script written. ScriptNode will auto-reload via FileSystemWatcher."
                    });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new { success = false, error = $"Failed to write file: {ex.Message}" });
                }
            });
        }

        [McpTool]
        [Description("Read the gh_errors.log file adjacent to a ScriptNode's Python script. Contains full tracebacks from the last execution failure.")]
        public string GetErrorLog(
            [Description("GUID of the ScriptNode component")] string component_id)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (!Guid.TryParse(component_id, out var guid))
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });

                if (!_server.RegisteredNodes.TryGetValue(guid, out var node))
                    return JsonConvert.SerializeObject(new { success = false, error = $"ScriptNode not found: {component_id}" });

                var scriptPath = node.ScriptPath;
                if (string.IsNullOrEmpty(scriptPath))
                    return JsonConvert.SerializeObject(new { success = false, error = "ScriptNode has no script_path set" });

                var logPath = Path.Combine(Path.GetDirectoryName(scriptPath), "gh_errors.log");
                if (!File.Exists(logPath))
                    return JsonConvert.SerializeObject(new { success = true, exists = false, message = "No error log found (no errors have occurred)." });

                var content = File.ReadAllText(logPath);
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    exists = true,
                    path = logPath,
                    content
                }, Formatting.Indented);
            });
        }

        // ── Helper ───────────────────────────────────────────────────
        private object BuildAlienNodeInfo(AlienNodeComponent node)
        {
            var runtimeMessages = new List<object>();
            foreach (var msg in node.RuntimeMessages(GH_RuntimeMessageLevel.Error))
                runtimeMessages.Add(new { level = "Error", message = msg });
            foreach (var msg in node.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
                runtimeMessages.Add(new { level = "Warning", message = msg });
            foreach (var msg in node.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
                runtimeMessages.Add(new { level = "Remark", message = msg });

            var inputs = new List<object>();
            // Skip index 0 (script_path)
            for (int i = 1; i < node.Params.Input.Count; i++)
            {
                var p = node.Params.Input[i];
                var sources = p.Sources.Select(s => new
                {
                    sourceComponentId = s.Attributes.GetTopLevel.DocObject.InstanceGuid.ToString(),
                    sourceParamName = s.Name
                }).ToList();
                
                inputs.Add(new 
                { 
                    name = p.Name, 
                    type = p.TypeName, 
                    sourceCount = p.SourceCount,
                    sources = sources
                });
            }

            var outputs = new List<object>();
            foreach (var p in node.Params.Output)
            {
                var recipients = p.Recipients.Select(r => new
                {
                    recipientComponentId = r.Attributes.GetTopLevel.DocObject.InstanceGuid.ToString(),
                    recipientParamName = r.Name
                }).ToList();

                outputs.Add(new 
                { 
                    name = p.Name, 
                    type = p.TypeName, 
                    recipientCount = p.Recipients.Count,
                    recipients = recipients
                });
            }

            var header = node.CurrentHeader;

            return new
            {
                id = node.InstanceGuid.ToString(),
                name = node.NickName,
                scriptPath = node.ScriptPath ?? "(none)",
                hasFileWatcher = !string.IsNullOrEmpty(node.ScriptPath),
                headerInputs = (object)header?.Inputs?.Select(inp => new { inp.Name, inp.TypeHint, inp.IsList, meta = inp.Meta.Description }).ToList() ?? new List<object>(),
                headerOutputs = (object)header?.Outputs?.Select(o => new { o.Name, o.TypeHint, meta = o.Meta.Description }).ToList() ?? new List<object>(),
                liveMode = node.LiveMode,
                pendingChanges = node.HasPendingChanges,
                runtimeMessageLevel = node.RuntimeMessageLevel.ToString(),
                runtimeMessages,
                inputs,
                outputs
            };
        }
    }
}
