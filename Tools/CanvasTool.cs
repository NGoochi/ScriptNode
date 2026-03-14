using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Newtonsoft.Json;

namespace ScriptNodePlugin.Tools
{
    /// <summary>
    /// MCP tools for inspecting the Grasshopper canvas.
    /// </summary>
    [McpToolClass]
    public class CanvasTool
    {
        private readonly GrasshopperContext _ctx;
        private readonly McpServer _server;

        public CanvasTool(GrasshopperContext ctx, McpServer server)
        {
            _ctx = ctx;
            _server = server;
        }

        [McpTool]
        [Description("List all components on the Grasshopper canvas with their names, types, GUIDs, positions, connections (wires), and runtime status (OK/Warning/Error). Use this to understand the full node graph.")]
        public string GetCanvasInfo()
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                var doc = _ctx.GetActiveDocument();
                if (doc == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "No active Grasshopper document" });

                var components = new List<object>();

                foreach (var obj in doc.Objects)
                {
                    if (obj == null || obj.Attributes == null) continue;

                    var info = new Dictionary<string, object>
                    {
                        ["id"] = obj.InstanceGuid.ToString(),
                        ["name"] = obj.Name ?? "",
                        ["nickname"] = obj.NickName ?? "",
                        ["type"] = obj.GetType().Name,
                        ["x"] = obj.Attributes.Pivot.X,
                        ["y"] = obj.Attributes.Pivot.Y
                    };

                    if (obj is IGH_Component comp)
                    {
                        info["category"] = comp.Category;
                        info["subcategory"] = comp.SubCategory;
                        info["runtimeMessageLevel"] = comp.RuntimeMessageLevel.ToString();
                        info["isScriptNode"] = comp is ScriptNodeComponent;

                        // Inputs with their sources
                        var inputs = new List<object>();
                        foreach (var inp in comp.Params.Input)
                        {
                            var sources = new List<object>();
                            foreach (var src in inp.Sources)
                            {
                                var srcParent = src.Attributes?.GetTopLevel?.DocObject;
                                sources.Add(new
                                {
                                    sourceComponentId = srcParent?.InstanceGuid.ToString() ?? "",
                                    sourceParamName = src.Name
                                });
                            }
                            inputs.Add(new
                            {
                                name = inp.Name,
                                nickname = inp.NickName,
                                type = inp.TypeName,
                                sourceCount = inp.SourceCount,
                                sources
                            });
                        }
                        info["inputs"] = inputs;

                        // Outputs with their recipients
                        var outputs = new List<object>();
                        foreach (var outp in comp.Params.Output)
                        {
                            var recipients = new List<object>();
                            foreach (var rec in outp.Recipients)
                            {
                                var recParent = rec.Attributes?.GetTopLevel?.DocObject;
                                recipients.Add(new
                                {
                                    recipientComponentId = recParent?.InstanceGuid.ToString() ?? "",
                                    recipientParamName = rec.Name
                                });
                            }
                            outputs.Add(new
                            {
                                name = outp.Name,
                                nickname = outp.NickName,
                                type = outp.TypeName,
                                recipientCount = outp.Recipients.Count,
                                recipients
                            });
                        }
                        info["outputs"] = outputs;
                    }
                    else if (obj is IGH_Param param)
                    {
                        info["sourceCount"] = param.SourceCount;
                        info["recipientCount"] = param.Recipients.Count;
                        info["dataCount"] = param.VolatileDataCount;
                    }

                    components.Add(info);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    documentName = doc.DisplayName,
                    componentCount = components.Count,
                    components
                }, Formatting.Indented);
            });
        }

        [McpTool]
        [Description("Read the actual output values from a component's output parameters. Provide the component GUID. Returns the data flowing through each output wire.")]
        public string GetComponentOutputs(
            [Description("GUID of the component to read outputs from")] string component_id)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                var doc = _ctx.GetActiveDocument();
                if (doc == null)
                    return JsonConvert.SerializeObject(new { success = false, error = "No active Grasshopper document" });

                if (!Guid.TryParse(component_id, out var guid))
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });

                var obj = doc.FindObject(guid, true);
                if (obj == null)
                    return JsonConvert.SerializeObject(new { success = false, error = $"Component not found: {component_id}" });

                var outputs = new List<object>();

                IList<IGH_Param> outputParams = null;
                if (obj is IGH_Component comp)
                    outputParams = comp.Params.Output;
                else if (obj is IGH_Param p)
                    outputParams = new List<IGH_Param> { p };

                if (outputParams != null)
                {
                    foreach (var outp in outputParams)
                    {
                        var values = new List<string>();
                        var data = outp.VolatileData;
                        if (data != null)
                        {
                            foreach (var path in data.Paths)
                            {
                                var branch = data.get_Branch(path);
                                if (branch != null)
                                {
                                    foreach (var item in branch)
                                    {
                                        values.Add(item?.ToString() ?? "null");
                                    }
                                }
                            }
                        }

                        outputs.Add(new
                        {
                            name = outp.Name,
                            nickname = outp.NickName,
                            type = outp.TypeName,
                            dataCount = outp.VolatileDataCount,
                            values = values.Take(100).ToList(), // Cap at 100 values to avoid huge payloads
                            truncated = values.Count > 100
                        });
                    }
                }

                return JsonConvert.SerializeObject(new { success = true, outputs }, Formatting.Indented);
            });
        }
    }
}
