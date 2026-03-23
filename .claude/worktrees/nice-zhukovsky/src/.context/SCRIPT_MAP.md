# SCRIPT_MAP.md
### Current Files and Purpose

**THIS FILE IS VOLATILE. Rewrite freely when direction changes.**

---

## Status: Phase 1 + Phase 2 Complete

### Core Source Files

| File | Purpose | Status |
|------|---------|--------|
| `src/ScriptNodePlugin.csproj` | Project file, net7.0, outputs .gha | done |
| `src/ScriptNodeInfo.cs` | GH_AssemblyInfo metadata | done |
| `src/ScriptNodeComponent.cs` | Main component (IGH_VariableParameterComponent, wire preservation, MCP registration) | done |
| `src/ScriptFileWatcher.cs` | FileSystemWatcher wrapper with 150ms debounce + timestamp fallback | done |
| `src/HeaderParser.cs` | NODE_INPUTS/OUTPUTS parser, type→Param mapping, change detection | done |
| `src/PythonExecutor.cs` | Python execution via Rhino.Runtime.PythonScript, error logging | done |

### MCP Server Files

| File | Purpose | Status |
|------|---------|--------|
| `src/McpServer.cs` | Streamable HTTP MCP server, singleton, auto-start, tool registration | done |
| `src/GrasshopperContext.cs` | UI thread marshalling (ExecuteOnUiThread), active document access | done |
| `src/Tools/CanvasTool.cs` | get_canvas_info, get_component_outputs | done |
| `src/Tools/ScriptNodeTool.cs` | get_scriptnode_info, get_script_source, write_script_source, get_error_log | done |
| `src/Tools/RhinoAppTool.cs` | get_rhino_command_history, clear_rhino_command_history, run_rhino_command | done |

### Scripting Documentation (user/agent-facing)

| File | Purpose | Status |
|------|---------|--------|
| `scripting/FIRST_PROMPT.md` | Master prompt for scripting agents | done |
| `scripting/HEADER_PROTOCOL.md` | Input/output header syntax reference | done |
| `scripting/TYPE_LEXICON.md` | Complete type mapping (Python↔GH↔RhinoCommon) | done |
| `scripting/SCRIPT_TEMPLATE.py` | Annotated copy-paste starter | done |
| `scripting/MCP_WORKFLOW.md` | 9 MCP tools, debug protocol, example responses | done |
| `scripting/CHAINING.md` | How scripts connect in GH | done |
| `scripting/GOTCHAS.md` | Platform diffs, known failures, common errors | done |
| `scripting/ALGORITHMS.md` | Generative design systems reference | done |
| `scripting/PROJECT_CONTEXT.md` | Carlton tower design brief | done |
| `scripting/examples/simple_add.py` | Minimal test script | done |
| `scripting/examples/voxel_grid.py` | 3D voxel grid example | done |

### Setup / Installation

| File | Purpose | Status |
|------|---------|--------|
| `setup/SETUP.md` | Agent-readable install + config guide | done |
| `setup/install_windows.bat` | Windows install helper | done |
| `setup/install_macos.sh` | macOS install helper | done |

### Plugin Dev Bible (.context/)

| File | Purpose | Status |
|------|---------|--------|
| `src/.context/HANDOFF.md` | Full plugin dev brief | revised |
| `src/.context/CONVENTIONS.md` | C# coding conventions | current |
| `src/.context/ARCHITECTURE.md` | Class diagram + data flow | revised |
| `src/.context/GH_SDK_GUIDE.md` | GH SDK patterns + gotchas | current |
| `src/.context/DEVELOPMENT.md` | Build/deploy workflow, wire preservation details | current |
| `src/.context/REFERENCES.md` | Reference repos and forum threads | current |
| `src/.context/SCRIPT_MAP.md` | This file | revised |
| `src/.context/JOURNAL.md` | Append-only session log | updated |

### Build Output

| File | Purpose |
|------|---------|
| `src/bin/Release/net7.0/ScriptNodePlugin.gha` | Compiled plugin (26+ KB) |
