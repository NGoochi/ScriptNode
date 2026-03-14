using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;

using GH_IO.Serialization;
using Rhino;

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
        // ── State ──────────────────────────────────────────────
        private string _scriptPath;
        private string _lastSource;
        private ScriptHeader _currentHeader;
        private DateTime _lastFileWrite = DateTime.MinValue;
        private ScriptFileWatcher _watcher;
        private bool _isRebuildScheduled;

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
            if (!DA.GetData(SCRIPT_PATH_INDEX, ref path)) return;

            // Normalise
            if (!string.IsNullOrWhiteSpace(path))
                path = Path.GetFullPath(path);

            // 2. Handle script_path change
            if (path != _scriptPath)
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
                        // Schedule param rebuild OUTSIDE the solve
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
        /// Rebuild dynamic input/output parameters to match the current header.
        /// This is called from ScheduleSolution callback (safe to modify params).
        /// Only changes params that are actually different to preserve wires.
        /// </summary>
        private void RebuildParameters()
        {
            if (_currentHeader == null) return;

            RebuildDynamicInputs(_currentHeader.Inputs);
            RebuildDynamicOutputs(_currentHeader.Outputs);

            Params.OnParametersChanged();
            VariableParameterMaintenance();
        }

        private void RebuildDynamicInputs(List<InputDef> expected)
        {
            // Current dynamic inputs (skip index 0 = script_path)
            var current = Params.Input.Skip(1).ToList();

            // Build target list
            var targetParams = new List<IGH_Param>();
            foreach (var def in expected)
            {
                // Check if an existing param at this position has the same name and compatible type
                var existing = current.FirstOrDefault(p => p.Name == def.Name);
                if (existing != null && IsCompatibleParam(existing, def))
                {
                    // Keep existing param (preserves wires!)
                    // But update access if it changed
                    var wantedAccess = def.IsList ? GH_ParamAccess.list : GH_ParamAccess.item;
                    if (existing.Access != wantedAccess)
                        existing.Access = wantedAccess;
                    targetParams.Add(existing);
                }
                else
                {
                    // Create new param
                    var newParam = HeaderParser.CreateParamForType(def.TypeHint, def.Name, def.IsList);
                    newParam.CreateAttributes();
                    targetParams.Add(newParam);
                }
            }

            // Remove all dynamic inputs (from the end to avoid index shifting)
            while (Params.Input.Count > 1)
            {
                var toRemove = Params.Input[Params.Input.Count - 1];
                toRemove.RemoveAllSources();
                Params.Input.RemoveAt(Params.Input.Count - 1);
            }

            // Add target params
            foreach (var p in targetParams)
            {
                if (p.Attributes == null) p.CreateAttributes();
                Params.RegisterInputParam(p);
            }
        }

        private void RebuildDynamicOutputs(List<string> expected)
        {
            var current = Params.Output.ToList();

            var targetParams = new List<IGH_Param>();
            foreach (var name in expected)
            {
                var existing = current.FirstOrDefault(p => p.Name == name);
                if (existing != null)
                {
                    // Keep existing (preserves wires!)
                    targetParams.Add(existing);
                }
                else
                {
                    var newParam = HeaderParser.CreateOutputParam(name);
                    newParam.CreateAttributes();
                    targetParams.Add(newParam);
                }
            }

            // Remove all outputs
            while (Params.Output.Count > 0)
            {
                var toRemove = Params.Output[Params.Output.Count - 1];
                toRemove.RemoveAllSources();
                Params.Output.RemoveAt(Params.Output.Count - 1);
            }

            // Add target outputs
            foreach (var p in targetParams)
            {
                if (p.Attributes == null) p.CreateAttributes();
                Params.RegisterOutputParam(p);
            }
        }

        /// <summary>
        /// Check if an existing param is compatible with a new input definition.
        /// Compatible means same param type family.
        /// </summary>
        private static bool IsCompatibleParam(IGH_Param existing, InputDef def)
        {
            // Create a temp param of the expected type and compare type names
            var expected = HeaderParser.CreateParamForType(def.TypeHint, def.Name, def.IsList);
            return existing.GetType() == expected.GetType();
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

        // ── Cleanup ────────────────────────────────────────────
        public override void RemovedFromDocument(GH_Document document)
        {
            _watcher?.Dispose();
            _watcher = null;
            base.RemovedFromDocument(document);
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

        protected override Bitmap Icon => null; // v1: no custom icon

        public override GH_Exposure Exposure => GH_Exposure.primary;
    }
}
