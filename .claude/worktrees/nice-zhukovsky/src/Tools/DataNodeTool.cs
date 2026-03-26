using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Newtonsoft.Json;

namespace ScriptNodePlugin.Tools
{
    /// <summary>
    /// MCP tools for interacting with DataNode components.
    /// Follows the same pattern as ScriptNodeTool.cs:
    /// - [McpToolClass] on the class
    /// - [McpTool] + [Description] on each tool method
    /// - [Description] on each parameter
    /// - Method names auto-converted to snake_case by DiscoverTools()
    /// - Returns JSON string
    /// </summary>
    [McpToolClass]
    public class DataNodeTool
    {
        private readonly GrasshopperContext _ctx;
        private readonly McpServer _server;

        public DataNodeTool(GrasshopperContext ctx, McpServer server)
        {
            _ctx    = ctx;
            _server = server;
        }

        // ── get_datanode_info ─────────────────────────────────
        [McpTool]
        [Description("Get detailed information about a DataNode component: schema (fields with types/ranges), " +
                      "item count, all item names and values, and which overrides are enabled. " +
                      "If no component_id is provided, returns info for all registered DataNodes.")]
        public string GetDatanodeInfo(
            [Description("Optional GUID of a specific DataNode. Omit to get info for all DataNodes.")]
            string component_id = null)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (!string.IsNullOrEmpty(component_id))
                {
                    if (!Guid.TryParse(component_id, out Guid guid))
                        return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });

                    if (!_server.RegisteredDataNodes.TryGetValue(guid, out var node))
                        return JsonConvert.SerializeObject(new { success = false, error = "DataNode not found. Use get_canvas_info to find component GUIDs." });

                    return JsonConvert.SerializeObject(new { success = true, node = BuildNodeInfo(node) }, Formatting.Indented);
                }

                // All registered DataNodes
                var nodes = _server.RegisteredDataNodes.Values
                    .Select(n => BuildNodeInfo(n))
                    .ToList();

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = nodes.Count,
                    nodes
                }, Formatting.Indented);
            });
        }

        // ── set_datanode_values ───────────────────────────────
        [McpTool]
        [Description("Set values for specific items in a DataNode. " +
                      "Identify items by index (0-based) or name.")]
        public string SetDatanodeValues(
            [Description("GUID of the DataNode component")]
            string component_id,
            [Description("Item identifier — either 0-based index (as string number) or the item name")]
            string item,
            [Description("Field name to set")]
            string field,
            [Description("Value to set (number or string)")]
            string value)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (!Guid.TryParse(component_id, out Guid guid))
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });
                if (!_server.RegisteredDataNodes.TryGetValue(guid, out var node))
                    return JsonConvert.SerializeObject(new { success = false, error = "DataNode not found" });

                var schema = node.Schema;
                var fieldDef = schema.Fields.FirstOrDefault(f => f.Name == field);
                if (fieldDef == null)
                    return JsonConvert.SerializeObject(new { success = false, error = $"Field '{field}' not found. Available: {string.Join(", ", schema.Fields.Select(f => f.Name))}" });

                // Find item by index or name
                ItemRecord targetItem = null;
                if (int.TryParse(item, out int idx) && idx >= 0 && idx < schema.Items.Count)
                {
                    targetItem = schema.Items[idx];
                }
                else
                {
                    targetItem = schema.Items.FirstOrDefault(i =>
                        string.Equals(i.Name, item, StringComparison.OrdinalIgnoreCase));
                }

                if (targetItem == null)
                    return JsonConvert.SerializeObject(new { success = false, error = $"Item '{item}' not found. Available: {string.Join(", ", schema.Items.Select(i => i.Name))}" });

                // Parse and set value
                if (double.TryParse(value, out double numVal))
                    targetItem.SetValue(fieldDef, numVal);
                else
                    targetItem.Values[field] = value;

                node.RequestRecompute();
                return JsonConvert.SerializeObject(new { success = true, item = targetItem.Name, field = field, value = targetItem.Values.GetValueOrDefault(field) });
            });
        }

        // ── add_datanode_items ────────────────────────────────
        [McpTool]
        [Description("Create new items in a DataNode. Items are added with default values (midpoint of each field's range).")]
        public string AddDatanodeItems(
            [Description("GUID of the DataNode component")]
            string component_id,
            [Description("Number of items to create")]
            string count,
            [Description("Name prefix for the new items (e.g. 'Level', 'Room')")]
            string name_prefix = "Item")
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (!Guid.TryParse(component_id, out Guid guid))
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });
                if (!_server.RegisteredDataNodes.TryGetValue(guid, out var node))
                    return JsonConvert.SerializeObject(new { success = false, error = "DataNode not found" });
                if (!int.TryParse(count, out int n) || n < 1 || n > 1000)
                    return JsonConvert.SerializeObject(new { success = false, error = "Count must be a number between 1 and 1000" });

                node.Schema.AddItems(n, name_prefix ?? "Item");
                node.RequestRecompute();

                return JsonConvert.SerializeObject(new { success = true, total_items = node.Schema.Items.Count, added = n });
            });
        }

        // ── set_datanode_schema ───────────────────────────────
        [McpTool]
        [Description("Add, remove, or modify fields in a DataNode's schema. " +
                      "Actions: 'add' (creates new field), 'remove' (deletes field), 'modify' (updates existing field).")]
        public string SetDatanodeSchema(
            [Description("GUID of the DataNode component")]
            string component_id,
            [Description("Action: 'add', 'remove', or 'modify'")]
            string action,
            [Description("Field name")]
            string field_name,
            [Description("Type hint (float, int, str, bool, Point3d, etc.) — required for 'add', optional for 'modify'")]
            string type = "float",
            [Description("Minimum value — optional")]
            string min = null,
            [Description("Maximum value — optional")]
            string max = null,
            [Description("Decimal places (0–5) — optional")]
            string decimals = null,
            [Description("Is parent field (true/false) — optional")]
            string is_parent = null)
        {
            return _ctx.ExecuteOnUiThread(() =>
            {
                if (!Guid.TryParse(component_id, out Guid guid))
                    return JsonConvert.SerializeObject(new { success = false, error = "Invalid GUID format" });
                if (!_server.RegisteredDataNodes.TryGetValue(guid, out var node))
                    return JsonConvert.SerializeObject(new { success = false, error = "DataNode not found" });

                var schema = node.Schema;

                switch (action?.ToLowerInvariant())
                {
                    case "add":
                        if (schema.Fields.Any(f => f.Name == field_name))
                            return JsonConvert.SerializeObject(new { success = false, error = $"Field '{field_name}' already exists" });

                        var newField = new FieldDef
                        {
                            Name = field_name,
                            TypeHint = type ?? "float",
                            Min = ParseDoubleOrDefault(min, 0.0),
                            Max = ParseDoubleOrDefault(max, 100.0),
                            DecimalPlaces = ParseIntOrDefault(decimals, 2),
                            IsParent = ParseBoolOrDefault(is_parent, false),
                        };
                        schema.AddField(newField);
                        node.RequestRebuild();
                        return JsonConvert.SerializeObject(new { success = true, action = "added", field = field_name });

                    case "remove":
                        if (!schema.Fields.Any(f => f.Name == field_name))
                            return JsonConvert.SerializeObject(new { success = false, error = $"Field '{field_name}' not found" });
                        schema.RemoveField(field_name);
                        node.RequestRebuild();
                        return JsonConvert.SerializeObject(new { success = true, action = "removed", field = field_name });

                    case "modify":
                        var existing = schema.Fields.FirstOrDefault(f => f.Name == field_name);
                        if (existing == null)
                            return JsonConvert.SerializeObject(new { success = false, error = $"Field '{field_name}' not found" });

                        if (type != null) existing.TypeHint = type;
                        if (min != null) existing.Min = ParseDoubleOrDefault(min, existing.Min);
                        if (max != null) existing.Max = ParseDoubleOrDefault(max, existing.Max);
                        if (decimals != null) existing.DecimalPlaces = ParseIntOrDefault(decimals, existing.DecimalPlaces);
                        if (is_parent != null) existing.IsParent = ParseBoolOrDefault(is_parent, existing.IsParent);

                        node.RequestRebuild();
                        return JsonConvert.SerializeObject(new { success = true, action = "modified", field = field_name });

                    default:
                        return JsonConvert.SerializeObject(new { success = false, error = "Action must be 'add', 'remove', or 'modify'" });
                }
            });
        }

        // ── Helpers ──────────────────────────────────────────
        private static object BuildNodeInfo(DataNodeComponent node)
        {
            var schema = node.Schema;
            return new
            {
                guid = node.InstanceGuid.ToString(),
                name = node.NickName ?? "DataNode",
                field_count = schema.Fields.Count,
                item_count = schema.Items.Count,
                fields = schema.Fields.Select(f => new
                {
                    name = f.Name,
                    type = f.TypeHint,
                    min = f.Min,
                    max = f.Max,
                    decimals = f.DecimalPlaces,
                    preset = f.Preset,
                    is_parent = f.IsParent,
                }).ToList(),
                items = schema.Items.Select((item, idx) => new
                {
                    index = idx,
                    name = item.Name,
                    values = item.Values,
                }).ToList(),
                overrides = schema.EnabledOverrides.Select(o => new
                {
                    field = o.FieldName,
                    item_index = o.ItemIndex,
                    param_name = o.ToParamName(schema),
                }).ToList(),
            };
        }

        private static double ParseDoubleOrDefault(string s, double def)
        {
            if (s == null) return def;
            return double.TryParse(s, out double val) ? val : def;
        }

        private static int ParseIntOrDefault(string s, int def)
        {
            if (s == null) return def;
            return int.TryParse(s, out int val) ? val : def;
        }

        private static bool ParseBoolOrDefault(string s, bool def)
        {
            if (s == null) return def;
            if (bool.TryParse(s, out bool val)) return val;
            return s == "1" || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
