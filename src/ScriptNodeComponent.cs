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


#pragma warning disable CA1416 // System.Drawing cross-platform in Rhino context

namespace ScriptNodePlugin
{
    /// <summary>
    /// ScriptNode — a Grasshopper component that references an external Python (.py)
    /// file, dynamically creates input/output parameters from header comments, watches
    /// for file changes, and executes the script on each solve.
    /// </summary>
    public class ScriptNodeComponent : GH_Component, IGH_VariableParameterComponent
    {
        // ── State ─────────────────────────────────────────────────
        private string _scriptPath;
        private string _lastSource;
        private ScriptHeader _currentHeader;
        private DateTime _lastFileWrite = DateTime.MinValue;
        private ScriptFileWatcher _watcher;
        private bool _isRebuildScheduled;

        // ── Public getters for MCP tools + Attributes ─────────────
        public string ScriptPath  => _scriptPath;
        public ScriptHeader CurrentHeader => _currentHeader;

        /// <summary>True while a param rebuild is scheduled (shows amber dot on Load File row).</summary>
        public bool IsReloading   => _isRebuildScheduled;

        // The permanent script_path input is always at index 0
        private const int SCRIPT_PATH_INDEX = 0;

        // ── Constructor ────────────────────────────────────────
        public ScriptNodeComponent()
            : base(
                "ScriptNode",
                "SN",
                "Runs an external Python script with auto-updating parameters.\n" +
                "Set the script_path input to a .py file path.\n" +
                "Edit and save the file — the node updates automatically.",
                "Script",
                "ScriptNode")
        {
        }

        // ── Parameter Registration ─────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter(
                "script_path", "path",
                "Full path to the .py script file",
                GH_ParamAccess.item, "");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // Outputs are fully dynamic — start empty
        }

        // ── SolveInstance ──────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // 1. Get script path
            string path = "";
            
            // If there's no wire, use the path we set via the button (if any).
            // Usually DA.GetData handles this if PersistentData is set, but just to be safe:
            bool hasWire = Params.Input[SCRIPT_PATH_INDEX].SourceCount > 0;
            
            if (!DA.GetData(SCRIPT_PATH_INDEX, ref path)) return;

            // If DA.GetData falls back to empty string but we have a valid _scriptPath
            if (string.IsNullOrEmpty(path) && !hasWire && !string.IsNullOrEmpty(_scriptPath))
                path = _scriptPath;

            // Normalise
            if (!string.IsNullOrWhiteSpace(path))
                path = Path.GetFullPath(path);

            // 2. Handle script_path change or uninitialized watcher
            if (path != _scriptPath || _watcher == null)
            {
                _scriptPath = path;
                SetupWatcher();
            }

            // 3. Validate file exists
            if (string.IsNullOrWhiteSpace(_scriptPath) || !File.Exists(_scriptPath))
            {
                if (!string.IsNullOrWhiteSpace(_scriptPath))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Script file not found: {_scriptPath}");
                Message = "(no script)";
                return;
            }

            // 4. Read file (with timestamp fallback check)
            bool fileChanged = false;
            try
            {
                var writeTime = File.GetLastWriteTimeUtc(_scriptPath);
                if (writeTime != _lastFileWrite)
                {
                    _lastSource = File.ReadAllText(_scriptPath);
                    _lastFileWrite = writeTime;
                    fileChanged = true;
                }
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Cannot read script: {ex.Message}");
                return;
            }

            // 5. Parse header and check for param changes
            if (fileChanged || _currentHeader == null)
            {
                var newHeader = HeaderParser.ParseSource(_lastSource);
                if (newHeader != null && !newHeader.Equals(_currentHeader))
                {
                    _currentHeader = newHeader;
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
                    return; // Don't execute yet — params are being rebuilt
                }
            }

            // 6. Set display message to filename
            Message = Path.GetFileName(_scriptPath);

            // 7. Collect input values
            var inputs = new Dictionary<string, object>();
            // Dynamic inputs start at index 1 (after script_path)
            for (int i = 1; i < Params.Input.Count; i++)
            {
                var param = Params.Input[i];
                var paramName = param.Name;

                if (param.Access == GH_ParamAccess.list)
                {
                    var list = new List<object>();
                    DA.GetDataList(i, list);
                    inputs[paramName] = list;
                }
                else
                {
                    object val = null;
                    DA.GetData(i, ref val);

                    // Unwrap GH_Goo to get the raw value
                    if (val is Grasshopper.Kernel.Types.IGH_Goo goo)
                    {
                        val = GooToObject(goo);
                    }

                    inputs[paramName] = val;
                }
            }

            // 8. Get output names from current header
            var outputNames = _currentHeader?.Outputs ?? new List<string>();

            // 9. Execute Python
            var result = PythonExecutor.Execute(_lastSource, inputs, outputNames, _scriptPath);

            // 10. Handle results
            if (!result.Success)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, result.ErrorMessage);
                if (!string.IsNullOrEmpty(result.Traceback))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, result.Traceback);
                }
                return;
            }

            // Print stdout as remarks
            if (!string.IsNullOrEmpty(result.StdOut))
            {
                var stdoutTrimmed = result.StdOut.Trim();
                if (!string.IsNullOrEmpty(stdoutTrimmed))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, stdoutTrimmed);
            }

            // 11. Set output values
            for (int i = 0; i < Params.Output.Count && i < outputNames.Count; i++)
            {
                var name = outputNames[i];
                if (result.Outputs.TryGetValue(name, out var val) && val != null)
                {
                    // Check if the value is a list — if so, set as list output
                    if (val is System.Collections.IList list)
                    {
                        var ghList = new List<object>();
                        foreach (var item in list) ghList.Add(item);
                        DA.SetDataList(i, ghList);
                    }
                    else
                    {
                        DA.SetData(i, val);
                    }
                }
            }
        }

        // ── Parameter Rebuild ──────────────────────────────────
        /// <summary>
        /// Rebuild all dynamic parameters while preserving wire connections.
        /// Uses the "Hops pattern": save live IGH_Param refs → unregister all →
        /// register new → restore connections by name match.
        /// Called from ScheduleSolution callback (safe to modify topology).
        /// </summary>
        private void RebuildParameters()
        {
            if (_currentHeader == null) return;

            // ── 1. Save existing connections (live IGH_Param refs) ───
            // The upstream/downstream param objects survive the rebuild because
            // they belong to OTHER components — only our params are destroyed.
            var savedInputSources = new Dictionary<string, List<IGH_Param>>();
            for (int i = 1; i < Params.Input.Count; i++)
            {
                var p = Params.Input[i];
                if (p.SourceCount > 0)
                    savedInputSources[p.NickName] = new List<IGH_Param>(p.Sources);
            }

            var savedOutputRecipients = new Dictionary<string, List<IGH_Param>>();
            for (int i = 0; i < Params.Output.Count; i++)
            {
                var p = Params.Output[i];
                if (p.Recipients.Count > 0)
                    savedOutputRecipients[p.NickName] = new List<IGH_Param>(p.Recipients);
            }

            // ── 2. Unregister all dynamic params ─────────────────────
            var inputsToRemove = Params.Input.Where(p => p.Name != "script_path").ToList();
            foreach (var p in inputsToRemove)
                Params.UnregisterInputParameter(p);

            var outputsToRemove = Params.Output.ToList();
            foreach (var p in outputsToRemove)
                Params.UnregisterOutputParameter(p);

            // ── 3. Register new params from header ───────────────────
            foreach (var def in _currentHeader.Inputs)
            {
                var newParam = HeaderParser.CreateParamForType(def.TypeHint, def.Name, def.IsList);
                newParam.CreateAttributes();
                Params.RegisterInputParam(newParam);
            }

            foreach (var name in _currentHeader.Outputs)
            {
                var newParam = HeaderParser.CreateOutputParam(name);
                newParam.CreateAttributes();
                Params.RegisterOutputParam(newParam);
            }

            Params.OnParametersChanged();
            VariableParameterMaintenance();

            // ── 4. Restore connections by name match ─────────────────
            // Wires reconnect when param names match. Renamed/removed params
            // intentionally lose their wires (correct behaviour).
            for (int i = 1; i < Params.Input.Count; i++)
            {
                var p = Params.Input[i];
                if (savedInputSources.TryGetValue(p.NickName, out var sources))
                    foreach (var source in sources)
                        p.AddSource(source);
            }

            // For outputs: recipients call AddSource on THEMSELVES
            // (GH wiring is always from the receiver's perspective)
            for (int i = 0; i < Params.Output.Count; i++)
            {
                var p = Params.Output[i];
                if (savedOutputRecipients.TryGetValue(p.NickName, out var recipients))
                    foreach (var recipient in recipients)
                        recipient.AddSource(p);
            }
        }

        // ── File Watcher ───────────────────────────────────────
        private void SetupWatcher()
        {
            if (_watcher == null)
            {
                _watcher = new ScriptFileWatcher(OnWatcherTriggered);
            }

            if (!string.IsNullOrEmpty(_scriptPath) && File.Exists(_scriptPath))
            {
                _watcher.Watch(_scriptPath);
            }
        }

        private void OnWatcherTriggered()
        {
            // Already on UI thread (ScriptFileWatcher handles marshalling)
            ExpireSolution(true);
        }

        // ── IGH_VariableParameterComponent ─────────────────────
        public bool CanInsertParameter(GH_ParameterSide side, int index)
        {
            // Users can't manually add params — only the script header controls them
            return false;
        }

        public bool CanRemoveParameter(GH_ParameterSide side, int index)
        {
            // Users can't manually remove params
            return false;
        }

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

        public bool DestroyParameter(GH_ParameterSide side, int index)
        {
            return true;
        }

        public void VariableParameterMaintenance()
        {
            // Ensure all dynamic params have valid attributes
            foreach (var p in Params.Input.Skip(1))
            {
                if (p.Attributes == null) p.CreateAttributes();
            }
            foreach (var p in Params.Output)
            {
                if (p.Attributes == null) p.CreateAttributes();
            }
        }

        // ── Serialization ──────────────────────────────────────
        public override bool Write(GH_IWriter writer)
        {
            writer.SetString("ScriptPath", _scriptPath ?? "");

            // Persist dynamic input definitions
            if (_currentHeader != null)
            {
                writer.SetInt32("InputCount", _currentHeader.Inputs.Count);
                for (int i = 0; i < _currentHeader.Inputs.Count; i++)
                {
                    writer.SetString($"Input_{i}_Name", _currentHeader.Inputs[i].Name);
                    writer.SetString($"Input_{i}_Type", _currentHeader.Inputs[i].TypeHint);
                    writer.SetBoolean($"Input_{i}_IsList", _currentHeader.Inputs[i].IsList);
                }

                writer.SetInt32("OutputCount", _currentHeader.Outputs.Count);
                for (int i = 0; i < _currentHeader.Outputs.Count; i++)
                {
                    writer.SetString($"Output_{i}_Name", _currentHeader.Outputs[i]);
                }
            }

            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            _scriptPath = reader.GetString("ScriptPath");

            // Restore header from saved state
            int inputCount = reader.ItemExists("InputCount") ? reader.GetInt32("InputCount") : 0;
            int outputCount = reader.ItemExists("OutputCount") ? reader.GetInt32("OutputCount") : 0;

            if (inputCount > 0 || outputCount > 0)
            {
                var inputs = new List<InputDef>();
                for (int i = 0; i < inputCount; i++)
                {
                    var name = reader.GetString($"Input_{i}_Name");
                    var type = reader.GetString($"Input_{i}_Type");
                    var isList = reader.GetBoolean($"Input_{i}_IsList");
                    inputs.Add(new InputDef(name, type, isList));
                }

                var outputs = new List<string>();
                for (int i = 0; i < outputCount; i++)
                {
                    outputs.Add(reader.GetString($"Output_{i}_Name"));
                }

                _currentHeader = new ScriptHeader(inputs, outputs);
            }

            return base.Read(reader);
        }

        // ── Context Menu ───────────────────────────────────────
        public override void AppendAdditionalMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);

            Menu_AppendItem(menu, "Open Script in Editor", (s, e) =>
            {
                if (!string.IsNullOrEmpty(_scriptPath) && File.Exists(_scriptPath))
                {
                    try { Process.Start(new ProcessStartInfo(_scriptPath) { UseShellExecute = true }); }
                    catch { }
                }
            }, !string.IsNullOrEmpty(_scriptPath) && File.Exists(_scriptPath));

            Menu_AppendItem(menu, "Reload Script", (s, e) =>
            {
                _lastFileWrite = DateTime.MinValue;
                _currentHeader = null;
                ExpireSolution(true);
            });

            Menu_AppendItem(menu, "Open Error Log", (s, e) =>
            {
                if (!string.IsNullOrEmpty(_scriptPath))
                {
                    var logPath = Path.Combine(
                        Path.GetDirectoryName(_scriptPath) ?? "",
                        "gh_errors.log");
                    if (File.Exists(logPath))
                    {
                        try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); }
                        catch { }
                    }
                }
            }, !string.IsNullOrEmpty(_scriptPath));
        }

        // ── MCP Auto-Start ─────────────────────────────────────
        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            McpServer.Instance.RegisterNode(this);
            if (!McpServer.Instance.IsRunning)
                McpServer.Instance.Start();

            // Make sure the file watcher is initialized for components loaded from a file
            SetupWatcher();
        }

        // ── Cleanup ────────────────────────────────────────────
        public override void RemovedFromDocument(GH_Document document)
        {
            McpServer.Instance.UnregisterNode(this);
            _watcher?.Dispose();
            _watcher = null;
            base.RemovedFromDocument(document);
        }

        // ── Custom Render: MCP Status Dot ─────────────────────
        public override void CreateAttributes()
        {
            m_attributes = new ScriptNodeAttributes(this);
        }

        // ── Utility ────────────────────────────────────────────
        /// <summary>
        /// Unwrap GH_Goo to get the underlying value for Python.
        /// </summary>
        private static object GooToObject(Grasshopper.Kernel.Types.IGH_Goo goo)
        {
            if (goo == null) return null;

            // Use the ScriptVariable method to extract the value
            var type = goo.GetType();
            var valueProp = type.GetProperty("Value");
            if (valueProp != null)
            {
                return valueProp.GetValue(goo);
            }

            return goo;
        }

        // ── Identity ───────────────────────────────────────────
        public override Guid ComponentGuid =>
            new Guid("A1B2C3D4-E5F6-7890-ABCD-EF0123456789");

        protected override Bitmap Icon
        {
            get
            {
                var bmp = new Bitmap(24, 24);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(Brushes.Black, 0, 0, 23, 23);
                    var red = Color.FromArgb(204, 34, 34);
                    var triangle = new PointF[] {
                        new PointF(12f, 5.5f),
                        new PointF(4.5f, 18f),
                        new PointF(19.5f, 18f)
                    };
                    g.FillPolygon(new SolidBrush(red), triangle);
                    g.FillEllipse(Brushes.White, 8f, 13.5f, 8f, 4.8f);
                    g.FillEllipse(new SolidBrush(red), 10.2f, 14.6f, 3.6f, 2.6f);
                }
                return bmp;
            }
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        // ── Load File (called by ScriptNodeAttributes on button click) ─
        /// <summary>
        /// Sets a new script path directly (e.g. from the Load File button).
        /// Clears cached state so the next SolveInstance picks it up fresh.
        /// </summary>
        public void SetScriptPath(string newPath)
        {
            _scriptPath   = newPath;

            // Ensure the input parameter contains the newly selected path
            // otherwise SolveInstance's DA.GetData will return the old value (or fail).
            if (Params.Input.Count > SCRIPT_PATH_INDEX)
            {
                var p = Params.Input[SCRIPT_PATH_INDEX];
                p.ClearData(); // Clear volatile data so it reads PersistentData again on next solve
                if (p is Grasshopper.Kernel.Parameters.Param_String pStr)
                {
                    pStr.PersistentData.Clear();
                    pStr.PersistentData.Append(new Grasshopper.Kernel.Types.GH_String(newPath));
                }
            }

            _lastFileWrite = DateTime.MinValue; // force re-read on next solve
            _lastSource    = null;
            _currentHeader = null;
            SetupWatcher();
            // Wire the script_path input param so it shows the path (cosmetic)
            ExpireSolution(true);
        }

    }
}
