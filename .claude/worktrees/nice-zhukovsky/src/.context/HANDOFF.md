# HANDOFF.md
### Plugin Development Brief — GH ScriptNode Plugin
**For:** Any LLM coding agent working on the C# plugin internals
**From:** Nick Gauci + Claude Opus, sessions of 2026-03-14 to 2026-03-15
**Read this entire document before modifying any C# code.**

---

## 0. What This Is

A custom Grasshopper plugin (.gha) for Rhino 8 that provides a single component: **ScriptNode**, plus an integrated MCP server for LLM agent communication.

ScriptNode references an external Python (.py) file. When that file changes on disk, the component re-reads it, parses header comments to determine inputs/outputs, dynamically rebuilds its parameters (preserving wire connections), executes the script, and pushes results to outputs.

The MCP server (9 tools across 3 classes) boots automatically when the first ScriptNode is placed, runs on `http://127.0.0.1:9876/mcp`, and gives external coding agents full diagnostic and editing access to the GH canvas.

---

## 1. Current Status

**Phase 1 (ScriptNode component): COMPLETE**
- Dynamic parameter rebuild from `# NODE_INPUTS:` / `# NODE_OUTPUTS:` headers
- FileSystemWatcher with 150ms debounce + timestamp fallback
- Python execution via `Rhino.Runtime.PythonScript.Create()`
- Wire preservation using the Hops pattern (dictionary cache + ScheduleSolution callback)
- Serialization (script path + param layout survives save/load)
- Error display via component runtime messages + `gh_errors.log`
- Cross-platform tested (Windows 11, macOS)

**Phase 2 (MCP server): COMPLETE**
- Streamable HTTP server on `127.0.0.1:9876`
- Auto-starts on first ScriptNode placement, shared across all instances
- Green dot indicator on component when server is active
- 9 tools across 3 classes (see Architecture section)

**What remains:**
- Edge cases in macOS FileSystemWatcher reliability
- Potential additions to MCP tool set (viewport capture, layer management)
- Performance optimization for large canvases in `get_canvas_info`

---

## 2. Architecture

### Component: ScriptNodeComponent

```
ScriptNodeComponent : GH_Component, IGH_VariableParameterComponent
├── Permanent input: script_path (string)
├── Dynamic inputs: rebuilt from NODE_INPUTS header
├── Dynamic outputs: rebuilt from NODE_OUTPUTS header
├── ScriptFileWatcher: FileSystemWatcher wrapper with debounce
├── HeaderParser: static methods, returns (name, type, isList) tuples
├── PythonExecutor: Rhino.Runtime.PythonScript wrapper
├── Wire preservation: Hops pattern in RebuildParameters()
└── Serialization: Write/Read override persists path + param layout
```

### MCP Server: McpServer + 3 Tool Classes

```
McpServer
├── Streamable HTTP on 127.0.0.1:9876
├── Auto-start on first ScriptNode AddedToDocument
├── Shared singleton — all ScriptNodes register with it
├── GrasshopperContext: UI thread marshalling wrapper
│
├── CanvasTool (2 tools)
│   ├── get_canvas_info — full canvas graph (components, wires, status)
│   └── get_component_outputs — read actual output data values
│
├── ScriptNodeTool (4 tools)
│   ├── get_scriptnode_info — deep-dive: path, header, messages, watcher
│   ├── get_script_source — read .py file contents
│   ├── write_script_source — write .py file (triggers auto-reload)
│   └── get_error_log — read gh_errors.log traceback
│
└── RhinoAppTool (3 tools)
    ├── get_rhino_command_history — read Rhino command line output
    ├── clear_rhino_command_history — clear for isolated testing
    └── run_rhino_command — execute Rhino command string
```

### Data Flow

```
File saved on disk
  → FileSystemWatcher fires (background thread)
  → Debounce (150ms)
  → RhinoApp.InvokeOnUiThread:
      → Re-read file
      → Parse header
      → If params changed:
          → SolveInstance detects mismatch
          → ScheduleSolution(5ms, RebuildCallback)
          → RebuildCallback: save wires → unregister → register → restore wires
          → Params.OnParametersChanged()
          → ExpireSolution(false)
      → If params unchanged: ExpireSolution(true) triggers re-solve only
  → SolveInstance:
      → Read inputs from GH params
      → PythonExecutor.Execute(source, inputs)
      → Write outputs to GH params
      → Display errors if any
```

---

## 3. Key Implementation Decisions

**PythonScript.Create() over Rhino.Runtime.Code:** The newer Code API lacks accessible compile-time references in the current Rhino 8 SDK NuGet packages. `PythonScript.Create()` is stable and well-documented.

**Param_GenericObject for inputs (with type-specific params where possible):** The HeaderParser maps type hints to specific GH param types (Param_Point, Param_Curve, etc.) for proper type casting on wires. Falls back to Param_GenericObject for unknown types.

**CanInsertParameter / CanRemoveParameter return false:** Only the file header controls the node interface. Users cannot manually add/remove params via right-click.

**MCP server in-process:** The server runs inside the Rhino/GH process (C#), not as a separate Python process. This avoids TCP socket complexity and gives direct access to the GH document. Uses `[McpTool]` attribute pattern for tool registration.

**Wire preservation via Hops pattern:** Cache `Sources`/`Recipients` by name before teardown, rebuild, restore via `AddSource()`. All topology changes happen in a `ScheduleSolution(5ms)` callback between solutions. See `DEVELOPMENT.md` for full details.

---

## 4. Technical Constraints

- Target `net7.0`
- RhinoCommon + Grasshopper via NuGet (version 8.0.23304.9001)
- Newtonsoft.Json for MCP response serialization
- All Rhino/GH API calls on UI thread — `RhinoApp.InvokeOnUiThread()` via `GrasshopperContext.ExecuteOnUiThread()`
- Cross-platform: `Path.Combine()` everywhere, handle `\r\n` and `\n`
- Post-build copy step in `.csproj` copies .gha to GH Libraries folder

---

## 5. Supported Types

| Header hint | GH Param | IsList → Access |
|---|---|---|
| `Point3d` | `Param_Point` | `list[Point3d]` → `GH_ParamAccess.list` |
| `Vector3d` | `Param_Vector` | same pattern |
| `Plane` | `Param_Plane` | |
| `Line` | `Param_Line` | |
| `Curve` | `Param_Curve` | |
| `Surface` | `Param_Surface` | |
| `Brep` | `Param_Brep` | |
| `Mesh` | `Param_Mesh` | |
| `int` | `Param_Integer` | |
| `float` | `Param_Number` | |
| `str` | `Param_String` | |
| `bool` | `Param_Boolean` | |
| `color` | `Param_Colour` | |
| `geometry` | `Param_GenericObject` | |

---

## 6. Project Structure

```
GHP_DynamicNode/
├── src/                                # C# plugin source
│   ├── ScriptNodePlugin.csproj
│   ├── ScriptNodeInfo.cs               # GH_AssemblyInfo
│   ├── ScriptNodeComponent.cs          # Main component
│   ├── ScriptFileWatcher.cs            # FileSystemWatcher wrapper
│   ├── HeaderParser.cs                 # NODE_INPUTS/OUTPUTS parser
│   ├── PythonExecutor.cs               # Python execution
│   ├── McpServer.cs                    # MCP server core
│   ├── GrasshopperContext.cs           # UI thread helper
│   ├── Tools/
│   │   ├── CanvasTool.cs              # Canvas inspection tools
│   │   ├── ScriptNodeTool.cs          # ScriptNode tools
│   │   └── RhinoAppTool.cs            # Rhino application tools
│   ├── bin/Release/net7.0/            # Build output
│   └── .context/                       # Plugin dev bible (this folder)
│       ├── HANDOFF.md                  # ← this file
│       ├── CONVENTIONS.md
│       ├── ARCHITECTURE.md
│       ├── GH_SDK_GUIDE.md
│       ├── DEVELOPMENT.md
│       ├── REFERENCES.md
│       ├── SCRIPT_MAP.md
│       └── JOURNAL.md
├── scripting/                          # User/agent-facing scripting docs
│   ├── FIRST_PROMPT.md
│   ├── HEADER_PROTOCOL.md
│   ├── TYPE_LEXICON.md
│   ├── SCRIPT_TEMPLATE.py
│   ├── MCP_WORKFLOW.md
│   ├── CHAINING.md
│   ├── GOTCHAS.md
│   ├── ALGORITHMS.md
│   ├── PROJECT_CONTEXT.md
│   └── examples/                       # Example scripts + user reference material
├── setup/
│   ├── SETUP.md                        # Agent-readable install guide
│   ├── install_windows.bat
│   └── install_macos.sh
└── README.md
```

---

## 7. Reference Material

See `REFERENCES.md` for the full list. Key references:
- Script-Sync source (file watcher pattern)
- HotLoader source (IGH_VariableParameterComponent)
- RhinoMCP IMPLEMENTATION.md (MCP architecture reference)
- McNeel Forum threads on programmatic script components
- MCP Python SDK (for any future external server work)

---

*End of HANDOFF.md. Last updated: 2026-03-15.*
