# ARCHITECTURE.md
### Plugin Architecture

---

## Class Overview

```
ScriptNodeComponent : GH_Component, IGH_VariableParameterComponent
├── Permanent input: script_path (string)
├── Dynamic inputs: rebuilt from NODE_INPUTS header
├── Dynamic outputs: rebuilt from NODE_OUTPUTS header
├── Uses: ScriptFileWatcher, HeaderParser, PythonExecutor
├── Properties:
│   ├── ScriptPath (string) — current script file path
│   ├── CurrentHeader (ParsedHeader) — last parsed header
│   └── InstanceGuid — used for MCP node registration
├── Wire preservation: RebuildParameters() using Hops pattern
├── Serialization: Write/Read persist script_path + param layout
├── MCP registration: registers with McpServer.RegisteredNodes on AddedToDocument
│
ScriptFileWatcher
├── Wraps FileSystemWatcher
├── Debounce logic (150ms)
├── Fallback: timestamp check on each solve
├── Thread-safe callback via RhinoApp.InvokeOnUiThread
├── Disposes on component removal
│
HeaderParser
├── Static methods to parse NODE_INPUTS / NODE_OUTPUTS
├── Returns ParsedHeader { Inputs: List<InputDef>, Outputs: List<string> }
├── InputDef: { Name, TypeHint, IsList }
├── Type mapping: string → GH Param type
├── Compares current params to expected params for change detection
│
PythonExecutor
├── Wraps Rhino.Runtime.PythonScript.Create()
├── Injects input values as named variables into exec namespace
├── Collects output values by name after execution
├── Captures exceptions, formats tracebacks
├── Writes errors to gh_errors.log adjacent to script file
│
McpServer (singleton)
├── Streamable HTTP server on 127.0.0.1:9876
├── Starts on first ScriptNode AddedToDocument
├── RegisteredNodes: Dictionary<Guid, ScriptNodeComponent>
├── GrasshopperContext: UI thread marshalling
├── Tool classes registered via [McpToolClass] attribute
│
GrasshopperContext
├── ExecuteOnUiThread<T>(Func<T>) — marshals to UI thread and returns result
├── GetActiveDocument() — returns current GH_Document
│
CanvasTool [McpToolClass]
├── GetCanvasInfo() — all components, wires, status
├── GetComponentOutputs(component_id) — actual output data values
│
ScriptNodeTool [McpToolClass]
├── GetScriptnodeInfo(component_id?) — deep node inspection
├── GetScriptSource(component_id) — read .py file
├── WriteScriptSource(component_id, content) — write .py file
├── GetErrorLog(component_id) — read gh_errors.log
│
RhinoAppTool [McpToolClass]
├── GetRhinoCommandHistory() — read command line
├── ClearRhinoCommandHistory() — clear command line
├── RunRhinoCommand(command) — execute Rhino command
```

---

## Data Flow: Script Execution

```
File saved on disk
  → FileSystemWatcher fires (background thread)
  → Debounce (150ms)
  → RhinoApp.InvokeOnUiThread:
      → Re-read file contents
      → HeaderParser.Parse(content)
      → Compare with current params
      → If header changed: set _needsRebuild flag
      → ExpireSolution(true)
  → SolveInstance runs:
      → If _needsRebuild:
          → ScheduleSolution(5ms, RebuildCallback)
          → return (skip computation this iteration)
      → Read input values from GH params
      → PythonExecutor.Execute(source, inputDict)
      → Write output values to GH params
      → AddRuntimeMessage if errors
      → Write traceback to gh_errors.log if errors
```

## Data Flow: Parameter Rebuild (Hops Pattern)

```
RebuildCallback (runs between solutions — topology safe):
  → 1. Cache: save new List<IGH_Param>(p.Sources) keyed by NickName
  →          save new List<IGH_Param>(p.Recipients) keyed by NickName
  → 2. Teardown: UnregisterInputParam / UnregisterOutputParam for all dynamic params
  → 3. Register: create new params from parsed header, RegisterInputParam / RegisterOutputParam
  → 4. Params.OnParametersChanged() + VariableParameterMaintenance()
  → 5. Restore: for inputs, newParam.AddSource(savedUpstream)
  →             for outputs, savedDownstream.AddSource(newOutput) (reversed!)
  → 6. _needsRebuild = false
  → 7. ExpireSolution(false) → new solution starts normally
```

## Data Flow: MCP Request

```
HTTP request to 127.0.0.1:9876/mcp
  → McpServer routes to appropriate tool method
  → Tool method calls GrasshopperContext.ExecuteOnUiThread(func)
  → func runs on UI thread with access to GH document
  → Result serialized as JSON via Newtonsoft.Json
  → HTTP response returned
```

---

## Threading Model

- **FileSystemWatcher callbacks:** Background thread → `RhinoApp.InvokeOnUiThread()` → UI thread
- **MCP HTTP requests:** HTTP thread → `GrasshopperContext.ExecuteOnUiThread()` → UI thread
- **SolveInstance:** Always on UI thread
- **ScheduleSolution callback:** Always on UI thread, between solutions
- **Rule:** ALL Rhino/GH API calls must be on UI thread. No exceptions.

---

*End of ARCHITECTURE.md. Last updated: 2026-03-15.*
