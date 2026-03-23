# JOURNAL.md
### Session Log (append-only)

---

## 2026-03-14 | Session 00 — Project Setup
**What happened:** Project context files created based on extensive R&D session. Attempted Python-only approaches (Script-Sync bridge, self-loading script component, programmatic param injection) — all failed at the same wall: Rhino 8's Script component param system (`ScriptVariableParam`) cannot be reliably created or modified from Python at runtime. Decision made to build a proper C# .gha plugin.
**Key learnings:**
- `IGH_VariableParameterComponent` is the sanctioned way to do dynamic params
- Never modify params during SolveInstance
- FileSystemWatcher is unreliable on macOS — always have a fallback
- Rhino 8 SR18+ has `SetSource()` / `SetParametersFromScript()` on script components but these are for the script component type specifically, not useful for a custom component
- `comp.CreateParameter()` produces the right param type for the calling component
- `RhinoApp.InvokeOnUiThread()` is mandatory for anything triggered from FileSystemWatcher
**Next:** Scaffold .csproj and get a blank component loading in GH

## 2026-03-14 | Session 01 — Phase 1 Build
**What happened:** Built all 6 source files for the ScriptNode plugin. Target: net7.0 with Grasshopper NuGet 8.0.23304.9001. Used PythonScript.Create() for Python execution (not the newer Rhino.Runtime.Code API which lacks accessible compile-time references). Fixed 3 compile errors during initial build: PythonScript.Output event signature (single string arg, not EventHandler), UnregisterInputParam/UnregisterOutputParam don't exist on GH_ComponentParamServer (used Params.Input.RemoveAt instead), and removed unused field.
**Key decisions:**
- Param rebuild only touches params that changed — preserves wires for unchanged inputs/outputs
- CanInsertParameter/CanRemoveParameter return false — only the file header controls the node interface
- ScheduleSolution callback used for safe param modification (never during SolveInstance)
- Serialization persists script_path + current param layout for save/load
- System.Windows.Forms referenced via net48 reference assemblies (same as Cordyceps)
**Build output:** `src/bin/Release/net7.0/ScriptNodePlugin.gha` (26 KB)
**Next:** Manual testing — load in Grasshopper, test with simple_add.py and voxel_grid.py

## 2026-03-15 | Session 02 — Wire Preservation Fix
**What happened:** Input wires disconnected on every header rebuild. Spent 7+ attempts debugging (same-callback restore, deferred ScheduleSolution, removing OnParametersChanged, RhinoApp.Idle handler, serialized GUIDs + FindObject, live IGH_Param refs) — ALL appeared to fail. Finally discovered the real problem: the `.csproj` had NO post-build copy step. Every `dotnet build` output to `bin\Release\` but GH loaded an old .gha from `%APPDATA%\Grasshopper\Libraries\`. Zero code changes were ever deployed. Added a `RhinoApp.WriteLine` at SolveInstance entry to confirm — no output. Compared file timestamps: Libraries file was 4 hours older.
**Key learnings:**
- **ALWAYS verify deployment first.** Compare `Libraries\ScriptNodePlugin.gha` timestamp against `bin\Release\` output. If mismatched, your code isn't loaded.
- **Close Rhino before building.** GH locks the .gha file at startup. Post-build copy fails silently if locked.
- `RhinoApp.WriteLine()` outputs to Rhino command line; `Debug.WriteLine()` outputs to VS debug window only (invisible without VS attached).
- The "Hops pattern" for wire preservation: save `new List<IGH_Param>(p.Sources)` keyed by NickName → unregister all → register new → `OnParametersChanged()` → `AddSource()` to restore. For outputs, reversed: `recipient.AddSource(newOutputParam)`.
- Never modify topology inside SolveInstance — always use `ScheduleSolution(5ms)` callback.
- Post-build copy step added to `.csproj` to prevent this class of error permanently.
**Changes:** Rewrote `RebuildParameters()` with Hops pattern (~80 lines). Removed dead `RebuildDynamicInputs`/`RebuildDynamicOutputs`/`IsCompatibleParam` (~120 lines). Added post-build copy target to `.csproj`. Created `DEVELOPMENT.md` with full dev guide.

## 2026-03-15 | Session 03 — MCP Server Integration
**What happened:** Built and integrated the MCP server directly into the plugin (C# in-process, not a separate Python server). Server auto-starts when first ScriptNode is placed, runs streamable HTTP on 127.0.0.1:9876. Implemented 9 tools across 3 classes: CanvasTool (get_canvas_info, get_component_outputs), ScriptNodeTool (get_scriptnode_info, get_script_source, write_script_source, get_error_log), RhinoAppTool (get_rhino_command_history, clear_rhino_command_history, run_rhino_command). All tools marshal to UI thread via GrasshopperContext.ExecuteOnUiThread(). Used [McpToolClass] / [McpTool] attribute pattern for tool registration. Tested with Antigravity, Claude Desktop, and Cursor — all connect successfully.
**Key decisions:**
- In-process C# server (not separate Python process) — direct GH document access, no TCP socket bridge needed
- Singleton pattern: one server shared by all ScriptNode instances on canvas
- Output values capped at 100 per parameter in get_component_outputs to prevent payload bloat
- Green dot indicator on component when server is active
**Build output:** Updated ScriptNodePlugin.gha with MCP server

## 2026-03-15 | Session 04 — Scripting Bible and Documentation Overhaul
**What happened:** Created the full user/agent-facing documentation suite in `scripting/`. 9 documents covering header protocol, type lexicon, MCP workflow, chaining, gotchas, algorithms, project context, plus script template and examples. Created `setup/` with SETUP.md (agent-readable install guide) and platform-specific install scripts. Revised all `src/.context/` files to reflect current state (MCP complete, wire preservation complete). Updated HANDOFF.md, ARCHITECTURE.md, SCRIPT_MAP.md.
**Key decisions:**
- Scripting docs live in `scripting/` (separate from plugin dev bible in `src/.context/`)
- `examples/` folder doubles as user reference material drop zone
- FIRST_PROMPT.md directs agents to also scan `src/.context/` for full context
- ALGORITHMS.md and PROJECT_CONTEXT.md are living documents — agents update them as the project evolves
- Setup targets 3 editors: Cursor, Antigravity, Claude Desktop
**Next:** Integration into actual repo tree. Workshop a prompt for the codespace agent to organise files into the root directory.
