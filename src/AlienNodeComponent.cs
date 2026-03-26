using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;

using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Parameters;

using GH_IO.Serialization;

using Newtonsoft.Json.Linq;

#pragma warning disable CA1416 // System.Drawing cross-platform in Rhino context

namespace ScriptNodePlugin
{
    /// <summary>
    /// Alien — unified external Python script node with manual parameter values and browser editor.
    /// </summary>
    public class AlienNodeComponent : GH_Component, IGH_VariableParameterComponent
    {
        private string _scriptPath;
        private string _lastSource;
        private ScriptHeader _currentHeader;
        private DateTime _lastFileWrite = DateTime.MinValue;
        private ScriptFileWatcher _watcher;
        private bool _isRebuildScheduled;

        private readonly ManualValueStore _manualValues = new ManualValueStore();
        private bool _liveMode;
        private bool _hasPendingChanges;

        public string ScriptPath => _scriptPath;
        public ScriptHeader CurrentHeader => _currentHeader;
        public ManualValueStore ManualValues => _manualValues;
        public bool LiveMode => _liveMode;
        public bool HasPendingChanges => _hasPendingChanges;
        public bool IsReloading => _isRebuildScheduled;

        private const int SCRIPT_PATH_INDEX = 0;

        public AlienNodeComponent()
            : base(
                "Alien",
                "Alien",
                "Runs an external Python script with dynamic pins and optional manual values.\n" +
                "Right-click → Edit Node… for the browser editor.",
                "Alien",
                "Alien")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter(
                "script_path", "path",
                "Full path to the .py script file",
                GH_ParamAccess.item, "");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string path = "";
            bool hasWire = Params.Input.Count > SCRIPT_PATH_INDEX
                && Params.Input[SCRIPT_PATH_INDEX].SourceCount > 0;

            if (!DA.GetData(SCRIPT_PATH_INDEX, ref path)) return;

            if (string.IsNullOrEmpty(path) && !hasWire && !string.IsNullOrEmpty(_scriptPath))
                path = _scriptPath;

            if (!string.IsNullOrWhiteSpace(path))
                path = Path.GetFullPath(path);

            if (path != _scriptPath || _watcher == null)
            {
                _scriptPath = path;
                SetupWatcher();
            }

            if (string.IsNullOrWhiteSpace(_scriptPath) || !File.Exists(_scriptPath))
            {
                if (!string.IsNullOrWhiteSpace(_scriptPath))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Script file not found: {_scriptPath}");
                Message = "(no script)";
                return;
            }

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
                    return;
                }
            }

            Message = Path.GetFileName(_scriptPath);
            if (_hasPendingChanges && !_liveMode)
                Message += " (pending changes)";

            var inputs = new Dictionary<string, object>();
            var headerInputs = _currentHeader?.Inputs ?? new List<InputDef>();

            for (int i = 1; i < Params.Input.Count; i++)
            {
                var param = Params.Input[i];
                var paramName = param.Name;
                var def = FindInputDef(paramName);
                if (string.IsNullOrEmpty(def.Name) && i - 1 < headerInputs.Count)
                    def = headerInputs[i - 1];

                if (param.SourceCount > 0)
                {
                    if (param.Access == GH_ParamAccess.list)
                    {
                        var list = new List<object>();
                        DA.GetDataList(i, list);
                        var unwrapped = new List<object>();
                        foreach (var item in list)
                        {
                            if (item is Grasshopper.Kernel.Types.IGH_Goo gooItem)
                                unwrapped.Add(GooToObject(gooItem));
                            else
                                unwrapped.Add(item);
                        }
                        inputs[paramName] = unwrapped;
                    }
                    else
                    {
                        object val = null;
                        DA.GetData(i, ref val);
                        if (val is Grasshopper.Kernel.Types.IGH_Goo goo)
                            val = GooToObject(goo);
                        inputs[paramName] = val;
                    }
                }
                else
                {
                    object resolved = _manualValues.ResolveValue(paramName,
                        string.IsNullOrEmpty(def.Name)
                            ? new InputDef(paramName, "object", false, ParamMetadata.Empty)
                            : def);
                    if (param.Access == GH_ParamAccess.list && resolved != null && !(resolved is List<object>))
                    {
                        if (resolved is System.Collections.IList il)
                        {
                            var ul = new List<object>();
                            foreach (var o in il) ul.Add(o);
                            inputs[paramName] = ul;
                        }
                        else
                            inputs[paramName] = new List<object> { resolved };
                    }
                    else
                        inputs[paramName] = resolved;
                }
            }

            var outputNames = _currentHeader?.Outputs?.Select(o => o.Name).ToList()
                ?? new List<string>();

            var result = PythonExecutor.Execute(_lastSource, inputs, outputNames, _scriptPath);

            if (!result.Success)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, result.ErrorMessage);
                if (!string.IsNullOrEmpty(result.Traceback))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, result.Traceback);
                return;
            }

            if (!string.IsNullOrEmpty(result.StdOut))
            {
                var stdoutTrimmed = result.StdOut.Trim();
                if (!string.IsNullOrEmpty(stdoutTrimmed))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, stdoutTrimmed);
            }

            for (int i = 0; i < Params.Output.Count && i < outputNames.Count; i++)
            {
                var name = outputNames[i];
                if (result.Outputs.TryGetValue(name, out var val) && val != null)
                {
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

        private void RebuildParameters()
        {
            if (_currentHeader == null) return;

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

            foreach (var p in Params.Input.Where(p => p.Name != "script_path").ToList())
                Params.UnregisterInputParameter(p);

            foreach (var p in Params.Output.ToList())
                Params.UnregisterOutputParameter(p);

            foreach (var def in _currentHeader.Inputs)
            {
                var newParam = HeaderParser.CreateParamForType(def.TypeHint, def.Name, def.IsList, def.Meta);
                newParam.CreateAttributes();
                Params.RegisterInputParam(newParam);
            }

            foreach (var od in _currentHeader.Outputs)
            {
                var newParam = HeaderParser.CreateOutputParam(od);
                newParam.CreateAttributes();
                Params.RegisterOutputParam(newParam);
            }

            Params.OnParametersChanged();
            VariableParameterMaintenance();

            for (int i = 1; i < Params.Input.Count; i++)
            {
                var p = Params.Input[i];
                if (savedInputSources.TryGetValue(p.NickName, out var sources))
                    foreach (var source in sources)
                        p.AddSource(source);
            }

            for (int i = 0; i < Params.Output.Count; i++)
            {
                var p = Params.Output[i];
                if (savedOutputRecipients.TryGetValue(p.NickName, out var recipients))
                    foreach (var recipient in recipients)
                        recipient.AddSource(p);
            }

            McpServer.Instance?.BroadcastAlienNodeState(this);
        }

        private void SetupWatcher()
        {
            if (_watcher == null)
                _watcher = new ScriptFileWatcher(OnWatcherTriggered);

            if (!string.IsNullOrEmpty(_scriptPath) && File.Exists(_scriptPath))
                _watcher.Watch(_scriptPath);
        }

        private void OnWatcherTriggered()
        {
            ExpireSolution(true);
        }

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
            foreach (var p in Params.Input.Skip(1))
            {
                if (p.Attributes == null) p.CreateAttributes();
            }
            foreach (var p in Params.Output)
            {
                if (p.Attributes == null) p.CreateAttributes();
            }
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetString("ScriptPath", _scriptPath ?? "");
            writer.SetBoolean("LiveMode", _liveMode);
            writer.SetBoolean("HasPendingChanges", _hasPendingChanges);

            if (_currentHeader != null)
            {
                writer.SetInt32("InputCount", _currentHeader.Inputs.Count);
                for (int i = 0; i < _currentHeader.Inputs.Count; i++)
                {
                    var inp = _currentHeader.Inputs[i];
                    writer.SetString($"Input_{i}_Name", inp.Name);
                    writer.SetString($"Input_{i}_Type", inp.TypeHint);
                    writer.SetBoolean($"Input_{i}_IsList", inp.IsList);
                    writer.SetString($"Input_{i}_MetaDesc", inp.Meta.Description ?? "");
                    if (inp.Meta.Min.HasValue) writer.SetDouble($"Input_{i}_MetaMin", inp.Meta.Min.Value);
                    if (inp.Meta.Max.HasValue) writer.SetDouble($"Input_{i}_MetaMax", inp.Meta.Max.Value);
                    if (inp.Meta.Step.HasValue) writer.SetDouble($"Input_{i}_MetaStep", inp.Meta.Step.Value);
                    if (inp.Meta.Decimals.HasValue) writer.SetInt32($"Input_{i}_MetaDec", inp.Meta.Decimals.Value);
                    if (inp.Meta.Default.HasValue) writer.SetDouble($"Input_{i}_MetaDef", inp.Meta.Default.Value);
                    if (!string.IsNullOrEmpty(inp.Meta.DrivenBy)) writer.SetString($"Input_{i}_MetaDrivenBy", inp.Meta.DrivenBy);
                }

                writer.SetInt32("OutputCount", _currentHeader.Outputs.Count);
                for (int i = 0; i < _currentHeader.Outputs.Count; i++)
                {
                    var o = _currentHeader.Outputs[i];
                    writer.SetString($"Output_{i}_Name", o.Name);
                    writer.SetString($"Output_{i}_Type", o.TypeHint ?? "");
                    writer.SetString($"Output_{i}_MetaDesc", o.Meta.Description ?? "");
                }
            }

            _manualValues.Write(writer);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            _scriptPath = reader.GetString("ScriptPath");
            _liveMode = reader.ItemExists("LiveMode") && reader.GetBoolean("LiveMode");
            _hasPendingChanges = reader.ItemExists("HasPendingChanges") && reader.GetBoolean("HasPendingChanges");

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
                    ParamMetadata meta = ParamMetadata.Empty;
                    if (reader.ItemExists($"Input_{i}_MetaDesc"))
                    {
                        var desc = reader.GetString($"Input_{i}_MetaDesc");
                        double? min = reader.ItemExists($"Input_{i}_MetaMin") ? reader.GetDouble($"Input_{i}_MetaMin") : (double?)null;
                        double? max = reader.ItemExists($"Input_{i}_MetaMax") ? reader.GetDouble($"Input_{i}_MetaMax") : (double?)null;
                        double? step = reader.ItemExists($"Input_{i}_MetaStep") ? reader.GetDouble($"Input_{i}_MetaStep") : (double?)null;
                        int? dec = reader.ItemExists($"Input_{i}_MetaDec") ? reader.GetInt32($"Input_{i}_MetaDec") : (int?)null;
                        double? def = reader.ItemExists($"Input_{i}_MetaDef") ? reader.GetDouble($"Input_{i}_MetaDef") : (double?)null;
                        string drivenBy = reader.ItemExists($"Input_{i}_MetaDrivenBy") ? reader.GetString($"Input_{i}_MetaDrivenBy") : null;
                        meta = new ParamMetadata(desc, min, max, step, dec, def, drivenBy);
                    }
                    inputs.Add(new InputDef(name, type, isList, meta));
                }

                var outputs = new List<OutputDef>();
                for (int i = 0; i < outputCount; i++)
                {
                    var name = reader.GetString($"Output_{i}_Name");
                    var typeHint = reader.ItemExists($"Output_{i}_Type") ? reader.GetString($"Output_{i}_Type") : "";
                    ParamMetadata ometa = ParamMetadata.Empty;
                    if (reader.ItemExists($"Output_{i}_MetaDesc"))
                        ometa = new ParamMetadata(reader.GetString($"Output_{i}_MetaDesc"), null, null, null);
                    outputs.Add(new OutputDef(name, typeHint, ometa));
                }

                _currentHeader = new ScriptHeader(inputs, outputs);
            }

            _manualValues.Read(reader);
            return base.Read(reader);
        }

        public override void AppendAdditionalMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);

            Menu_AppendItem(menu, "Edit Node…", (s, e) => OpenBrowserEditor(), true);

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

        public void OpenBrowserEditor()
        {
            try
            {
                if (!McpServer.Instance.IsRunning)
                    McpServer.Instance.Start();
                var port = McpServer.Instance.Port;
                var url = $"http://127.0.0.1:{port}/editor/?focus={InstanceGuid}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>Apply manual values from browser/MCP; optionally marks pending when not live.</summary>
        public void ApplyManualValuesFromDictionary(Dictionary<string, object> values, bool fromLive)
        {
            if (values == null) return;
            foreach (var kv in values)
            {
                var def = FindInputDef(kv.Key);
                var typeHint = !string.IsNullOrEmpty(def.Name) ? def.TypeHint : "object";
                var isList = !string.IsNullOrEmpty(def.Name) && def.IsList;
                _manualValues.SetRaw(kv.Key, ManualValueStore.SerializeForStore(kv.Value, typeHint, isList));
            }
            if (!fromLive && !_liveMode)
                _hasPendingChanges = true;
            else
            {
                _hasPendingChanges = false;
                ExpireSolution(true);
            }
            OnDisplayExpired(true);
            McpServer.Instance?.BroadcastAlienNodeState(this);
        }

        public void SetLiveMode(bool enabled)
        {
            _liveMode = enabled;
            McpServer.Instance?.BroadcastAlienNodeState(this);
        }

        public void ClearPendingAndRecompute()
        {
            _hasPendingChanges = false;
            ExpireSolution(true);
            McpServer.Instance?.BroadcastAlienNodeState(this);
        }

        public void SetSingleManualValue(string paramName, object value, bool fromLive)
        {
            var def = FindInputDef(paramName);
            var typeHint = !string.IsNullOrEmpty(def.Name) ? def.TypeHint : "object";
            var isList = !string.IsNullOrEmpty(def.Name) && def.IsList;
            _manualValues.SetRaw(paramName, ManualValueStore.SerializeForStore(value, typeHint, isList));
            if (!fromLive && !_liveMode)
                _hasPendingChanges = true;
            else
            {
                _hasPendingChanges = false;
                ExpireSolution(true);
            }
            OnDisplayExpired(true);
            McpServer.Instance?.BroadcastAlienNodeState(this);
        }

        public void StoreManualValueOnly(string paramName, object value)
        {
            var def = FindInputDef(paramName);
            var typeHint = !string.IsNullOrEmpty(def.Name) ? def.TypeHint : "object";
            var isList = !string.IsNullOrEmpty(def.Name) && def.IsList;
            _manualValues.SetRaw(paramName, ManualValueStore.SerializeForStore(value, typeHint, isList));
            _hasPendingChanges = true;
            OnDisplayExpired(true);
        }

        public JObject BuildNodeStateJson()
        {
            var arr = new JArray();
            var headerInputs = _currentHeader?.Inputs ?? new List<InputDef>();
            for (int i = 1; i < Params.Input.Count; i++)
            {
                var p = Params.Input[i];
                var def = FindInputDef(p.Name);
                _manualValues.TryGetRaw(p.Name, out var manualStr);
                bool wired = p.SourceCount > 0;
                var hasDef = !string.IsNullOrEmpty(def.Name);
                var obj = new JObject
                {
                    ["name"] = p.Name,
                    ["nickName"] = p.NickName,
                    ["direction"] = "input",
                    ["typeHint"] = hasDef ? def.TypeHint : p.TypeName,
                    ["isList"] = hasDef && def.IsList,
                    ["meta"] = hasDef ? def.Meta.Description : "",
                    ["manualValue"] = wired ? null : ManualValueToJson(manualStr),
                    ["isWired"] = wired,
                    ["liveEnabled"] = _liveMode,
                    ["min"] = hasDef && def.Meta.Min.HasValue ? JToken.FromObject(def.Meta.Min.Value) : null,
                    ["max"] = hasDef && def.Meta.Max.HasValue ? JToken.FromObject(def.Meta.Max.Value) : null,
                    ["step"] = hasDef && def.Meta.Step.HasValue ? JToken.FromObject(def.Meta.Step.Value) : null,
                    ["decimals"] = hasDef && def.Meta.Decimals.HasValue ? JToken.FromObject(def.Meta.Decimals.Value) : null,
                    ["default"] = hasDef && def.Meta.Default.HasValue ? JToken.FromObject(def.Meta.Default.Value) : null,
                    ["drivenBy"] = hasDef && !string.IsNullOrEmpty(def.Meta.DrivenBy) ? def.Meta.DrivenBy : null,
                };
                arr.Add(obj);
            }

            return new JObject
            {
                ["type"] = "state",
                ["guid"] = InstanceGuid.ToString(),
                ["params"] = arr,
                ["scriptPath"] = _scriptPath ?? "",
                ["liveMode"] = _liveMode,
                ["pendingChanges"] = _hasPendingChanges,
            };
        }

        private InputDef FindInputDef(string name)
        {
            if (_currentHeader == null || string.IsNullOrEmpty(name)) return default;
            foreach (var d in _currentHeader.Inputs)
            {
                if (d.Name == name) return d;
            }
            return default;
        }

        private static JToken ManualValueToJson(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return JValue.CreateNull();
            var t = raw.TrimStart();
            if (t.StartsWith("[", StringComparison.Ordinal) || t.StartsWith("{", StringComparison.Ordinal))
            {
                try { return JToken.Parse(raw); } catch { /* fall through */ }
            }
            if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return new JValue(d);
            if (bool.TryParse(raw, out var b))
                return new JValue(b);
            return new JValue(raw);
        }

        private static object GooToObject(Grasshopper.Kernel.Types.IGH_Goo goo)
        {
            if (goo == null) return null;
            var type = goo.GetType();
            var valueProp = type.GetProperty("Value");
            if (valueProp != null)
                return valueProp.GetValue(goo);
            return goo;
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            McpServer.Instance.RegisterAlienNode(this);
            if (!McpServer.Instance.IsRunning)
                McpServer.Instance.Start();
            SetupWatcher();

            if (McpServer.Instance.RegisteredAlienNodeCount == 1)
                OpenBrowserEditor();
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            McpServer.Instance.UnregisterAlienNode(this);
            _watcher?.Dispose();
            _watcher = null;
            base.RemovedFromDocument(document);
        }

        public override void CreateAttributes()
        {
            m_attributes = new AlienNodeAttributes(this);
        }

        public void SetScriptPath(string newPath)
        {
            _scriptPath = newPath;
            if (Params.Input.Count > SCRIPT_PATH_INDEX)
            {
                var p = Params.Input[SCRIPT_PATH_INDEX];
                p.ClearData();
                if (p is Param_String pStr)
                {
                    pStr.PersistentData.Clear();
                    pStr.PersistentData.Append(new Grasshopper.Kernel.Types.GH_String(newPath));
                }
            }
            _lastFileWrite = DateTime.MinValue;
            _lastSource = null;
            _currentHeader = null;
            SetupWatcher();
            ExpireSolution(true);
        }

        public override Guid ComponentGuid =>
            new Guid("C3D4E5F6-A7B8-9012-CDEF-012345678901");

        protected override Bitmap Icon
        {
            get
            {
                var bmp = new Bitmap(24, 24);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.FromArgb(26, 26, 26));
                    var green = Color.FromArgb(0, 200, 83);
                    using (var pen = new Pen(green, 2f))
                    {
                        g.DrawEllipse(pen, 3, 3, 18, 18);
                    }
                    using (var b = new SolidBrush(green))
                    {
                        g.FillEllipse(b, 9, 9, 6, 6);
                    }
                }
                return bmp;
            }
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;
    }
}
