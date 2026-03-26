using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;

using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Parameters;

using GH_IO.Serialization;

using Newtonsoft.Json;

#pragma warning disable CA1416 // System.Drawing cross-platform in Rhino context

namespace ScriptNodePlugin
{
    /// <summary>
    /// DataNode — a Grasshopper component that lets users define a data schema
    /// (fields with types/ranges), create N items conforming to that schema,
    /// and output each field as a list. Values are edited via an Eto.Forms editor.
    /// Each field can be individually overridden by wiring an input.
    /// </summary>
    public class DataNodeComponent : GH_Component, IGH_VariableParameterComponent
    {
        // ── State ─────────────────────────────────────────────────
        private DataNodeSchema _schema = new DataNodeSchema();
        private DataNodeSchema _lastBuiltSchema; // tracks what params were last built from
        private bool _isRebuildScheduled;

        // ── Public getters for MCP tools + Attributes ─────────────
        public DataNodeSchema Schema => _schema;
        public bool IsReloading => _isRebuildScheduled;

        // ── Constructor ────────────────────────────────────────
        public DataNodeComponent()
            : base(
                "DataNode",
                "DN",
                "Define a data schema with typed fields, create items with slider/text values,\n" +
                "and output each field as a list. Edit values in a visual editor.\n" +
                "Override any value by wiring an input.",
                "Script",
                "ScriptNode")
        {
        }

        // ── Parameter Registration ─────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // No permanent inputs — dynamic inputs are created from override toggles
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // Dynamic outputs created from schema fields
        }

        // ── SolveInstance ──────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            if (_schema == null || _schema.Fields.Count == 0)
            {
                Message = "(no schema)";
                return;
            }

            // Check if schema changed and params need rebuild
            if (_lastBuiltSchema == null || !_schema.SchemaEquals(_lastBuiltSchema))
            {
                if (!_isRebuildScheduled)
                {
                    _isRebuildScheduled = true;
                    var doc = OnPingDocument();
                    doc?.ScheduleSolution(5, d =>
                    {
                        _isRebuildScheduled = false;
                        RebuildParameters();
                        ExpireSolution(false);
                    });
                }
                return;
            }

            Message = $"{_schema.Fields.Count}F • {_schema.Items.Count}I";

            // Build output lists for each field
            int outputIdx = 0;
            foreach (var field in _schema.Fields)
            {
                if (outputIdx >= Params.Output.Count) break;

                var outputList = new List<object>();

                // Check for parent override (wired input replaces ALL items)
                object parentOverride = null;
                bool hasParentOverride = false;
                if (_schema.IsOverrideEnabled(field.Name, -1))
                {
                    int inputIdx = FindInputIndex(field.Name);
                    if (inputIdx >= 0 && Params.Input[inputIdx].SourceCount > 0)
                    {
                        // Try to get list data first
                        var list = new List<object>();
                        if (DA.GetDataList(inputIdx, list) && list.Count > 0)
                        {
                            // If a list is provided, use it directly as the output
                            for (int i = 0; i < _schema.Items.Count; i++)
                            {
                                var val = i < list.Count ? UnwrapGoo(list[i]) : UnwrapGoo(list.Last());
                                outputList.Add(val);
                            }
                            DA.SetDataList(outputIdx, outputList);
                            outputIdx++;
                            continue;
                        }
                        else
                        {
                            object singleVal = null;
                            if (DA.GetData(inputIdx, ref singleVal))
                            {
                                parentOverride = UnwrapGoo(singleVal);
                                hasParentOverride = true;
                            }
                        }
                    }
                }

                // Build list from item values, applying overrides
                for (int i = 0; i < _schema.Items.Count; i++)
                {
                    var item = _schema.Items[i];
                    object value;

                    // Check per-item override
                    bool hasItemOverride = false;
                    if (_schema.IsOverrideEnabled(field.Name, i))
                    {
                        var overrideKey = new OverrideKey { FieldName = field.Name, ItemIndex = i };
                        string paramName = overrideKey.ToParamName(_schema);
                        int inputIdx = FindInputIndex(paramName);
                        if (inputIdx >= 0 && Params.Input[inputIdx].SourceCount > 0)
                        {
                            object overrideVal = null;
                            if (DA.GetData(inputIdx, ref overrideVal))
                            {
                                value = UnwrapGoo(overrideVal);
                                hasItemOverride = true;
                                outputList.Add(value);
                                continue;
                            }
                        }
                    }

                    // Parent override applies to all items
                    if (hasParentOverride && !hasItemOverride)
                    {
                        outputList.Add(parentOverride);
                        continue;
                    }

                    // Default: use the stored value
                    if (IsNumericField(field))
                    {
                        value = item.GetNumericValue(field);
                    }
                    else
                    {
                        value = item.GetStringValue(field);
                    }
                    outputList.Add(value);
                }

                DA.SetDataList(outputIdx, outputList);
                outputIdx++;
            }
        }

        // ── Parameter Rebuild (Hops pattern) ──────────────────
        private void RebuildParameters()
        {
            // ── 1. Save existing connections ───────────────────
            var savedInputSources = new Dictionary<string, List<IGH_Param>>();
            foreach (var p in Params.Input)
            {
                if (p.SourceCount > 0)
                    savedInputSources[p.NickName] = new List<IGH_Param>(p.Sources);
            }

            var savedOutputRecipients = new Dictionary<string, List<IGH_Param>>();
            foreach (var p in Params.Output)
            {
                if (p.Recipients.Count > 0)
                    savedOutputRecipients[p.NickName] = new List<IGH_Param>(p.Recipients);
            }

            // ── 2. Unregister all params ──────────────────────
            var inputsToRemove = Params.Input.ToList();
            foreach (var p in inputsToRemove)
                Params.UnregisterInputParameter(p);

            var outputsToRemove = Params.Output.ToList();
            foreach (var p in outputsToRemove)
                Params.UnregisterOutputParameter(p);

            // ── 3. Register new params from schema ────────────

            // Inputs: one per enabled override
            foreach (var (key, paramName) in _schema.GetOverrideParamNames())
            {
                var field = _schema.Fields.FirstOrDefault(f => f.Name == key.FieldName);
                if (field == null) continue;

                var newParam = HeaderParser.CreateParamForType(
                    field.TypeHint, paramName, key.IsParent);
                newParam.Optional = true;
                newParam.CreateAttributes();
                Params.RegisterInputParam(newParam);
            }

            // Outputs: one per field in the schema (list access for DataNode)
            foreach (var field in _schema.Fields)
            {
                var newParam = HeaderParser.CreateParamForType(
                    field.TypeHint, field.Name, isList: true);
                newParam.Description = $"List of {field.Name} values (one per item)";
                newParam.Optional = false;
                newParam.CreateAttributes();
                Params.RegisterOutputParam(newParam);
            }

            Params.OnParametersChanged();
            VariableParameterMaintenance();

            // ── 4. Restore connections by name match ───────────
            foreach (var p in Params.Input)
            {
                if (savedInputSources.TryGetValue(p.NickName, out var sources))
                    foreach (var source in sources)
                        p.AddSource(source);
            }

            foreach (var p in Params.Output)
            {
                if (savedOutputRecipients.TryGetValue(p.NickName, out var recipients))
                    foreach (var recipient in recipients)
                        recipient.AddSource(p);
            }

            // Cache the schema state we built for
            _lastBuiltSchema = DataNodeSchema.FromJson(_schema.ToJson());
        }

        /// <summary>
        /// Trigger a parameter rebuild from external code (e.g. the editor dialog).
        /// Must be called from the UI thread.
        /// </summary>
        public void RequestRebuild()
        {
            _isRebuildScheduled = true;
            var doc = OnPingDocument();
            doc?.ScheduleSolution(5, d =>
            {
                _isRebuildScheduled = false;
                RebuildParameters();
                ExpireSolution(false);
            });
        }

        /// <summary>
        /// Expire the solution without rebuilding params (e.g. when only values changed).
        /// Uses ScheduleSolution to avoid blocking the UI thread.
        /// </summary>
        public void RequestRecompute()
        {
            var doc = OnPingDocument();
            if (doc != null)
            {
                doc.ScheduleSolution(10, d => { ExpireSolution(false); });
            }
            else
            {
                ExpireSolution(true);
            }
        }

        // ── IGH_VariableParameterComponent ─────────────────────
        public bool CanInsertParameter(GH_ParameterSide side, int index) => false;
        public bool CanRemoveParameter(GH_ParameterSide side, int index) => false;

        public IGH_Param CreateParameter(GH_ParameterSide side, int index)
        {
            var param = new Param_GenericObject();
            param.Name = $"param{index}";
            param.NickName = param.Name;
            param.Description = "Dynamic parameter";
            param.Access = GH_ParamAccess.item;
            param.Optional = true;
            return param;
        }

        public bool DestroyParameter(GH_ParameterSide side, int index) => true;

        public void VariableParameterMaintenance()
        {
            foreach (var p in Params.Input)
                if (p.Attributes == null) p.CreateAttributes();
            foreach (var p in Params.Output)
                if (p.Attributes == null) p.CreateAttributes();
        }

        // ── Serialization ──────────────────────────────────────
        public override bool Write(GH_IWriter writer)
        {
            writer.SetString("DataNodeSchema", _schema?.ToJson() ?? "{}");
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            var json = reader.ItemExists("DataNodeSchema")
                ? reader.GetString("DataNodeSchema")
                : null;
            _schema = DataNodeSchema.FromJson(json);
            return base.Read(reader);
        }

        // ── Context Menu ───────────────────────────────────────
        public override void AppendAdditionalMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);

            Menu_AppendItem(menu, "Edit Data…", (s, e) =>
            {
                DataNodeEditor.ShowEditor(this);
            });

            Menu_AppendSeparator(menu);

            Menu_AppendItem(menu, "Export to JSON…", (s, e) =>
            {
                var dlg = new Eto.Forms.SaveFileDialog
                {
                    Title = "Export DataNode Schema"
                };
                dlg.Filters.Add(new Eto.Forms.FileFilter("JSON files", ".json"));

                if (dlg.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) == Eto.Forms.DialogResult.Ok)
                {
                    try
                    {
                        File.WriteAllText(dlg.FileName, _schema.ToJson());
                    }
                    catch (Exception ex)
                    {
                        Rhino.RhinoApp.WriteLine($"[DataNode] Export failed: {ex.Message}");
                    }
                }
            });

            Menu_AppendItem(menu, "Import from JSON…", (s, e) =>
            {
                var dlg = new Eto.Forms.OpenFileDialog
                {
                    Title = "Import DataNode Schema"
                };
                dlg.Filters.Add(new Eto.Forms.FileFilter("JSON files", ".json"));

                if (dlg.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) == Eto.Forms.DialogResult.Ok)
                {
                    try
                    {
                        var json = File.ReadAllText(dlg.FileName);
                        _schema = DataNodeSchema.FromJson(json);
                        _lastBuiltSchema = null; // force param rebuild
                        ExpireSolution(true);
                    }
                    catch (Exception ex)
                    {
                        Rhino.RhinoApp.WriteLine($"[DataNode] Import failed: {ex.Message}");
                    }
                }
            });

            Menu_AppendSeparator(menu);

            Menu_AppendItem(menu, "Reload Parameters", (s, e) =>
            {
                _lastBuiltSchema = null;
                ExpireSolution(true);
            });
        }

        // ── MCP Auto-Start ─────────────────────────────────────
        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            McpServer.Instance.RegisterDataNode(this);
            if (!McpServer.Instance.IsRunning)
                McpServer.Instance.Start();
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            McpServer.Instance.UnregisterDataNode(this);
            base.RemovedFromDocument(document);
        }

        // ── Custom Render ──────────────────────────────────────
        public override void CreateAttributes()
        {
            m_attributes = new DataNodeAttributes(this);
        }

        // ── Utilities ──────────────────────────────────────────
        private int FindInputIndex(string paramName)
        {
            for (int i = 0; i < Params.Input.Count; i++)
            {
                if (Params.Input[i].Name == paramName || Params.Input[i].NickName == paramName)
                    return i;
            }
            return -1;
        }

        private static bool IsNumericField(FieldDef field)
        {
            switch (field.TypeHint?.ToLowerInvariant())
            {
                case "float":
                case "double":
                case "number":
                case "int":
                case "integer":
                    return true;
                default:
                    return false;
            }
        }

        private static object UnwrapGoo(object val)
        {
            if (val is Grasshopper.Kernel.Types.IGH_Goo goo)
            {
                var type = goo.GetType();
                var valueProp = type.GetProperty("Value");
                if (valueProp != null) return valueProp.GetValue(goo);
            }
            return val;
        }

        // ── Identity ───────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("B2C3D4E5-F6A7-8901-BCDE-F01234567890");

        protected override Bitmap Icon
        {
            get
            {
                // Same icon as ScriptNode but with BLUE accent instead of red
                var bmp = new Bitmap(24, 24);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(Brushes.Black, 0, 0, 23, 23);
                    var blue = Color.FromArgb(34, 102, 204); // #2266CC
                    var triangle = new PointF[] {
                        new PointF(12f, 5.5f),
                        new PointF(4.5f, 18f),
                        new PointF(19.5f, 18f)
                    };
                    g.FillPolygon(new SolidBrush(blue), triangle);
                    g.FillEllipse(Brushes.White, 8f, 13.5f, 8f, 4.8f);
                    g.FillEllipse(new SolidBrush(blue), 10.2f, 14.6f, 3.6f, 2.6f);
                }
                return bmp;
            }
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;
    }
}
