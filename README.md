# ScriptNode (Grasshopper Python Dynamic Node)

A Grasshopper plugin for Rhino 8 that automatically syncs and rebuilds its parameters from an external Python script. 

Write complex Python logic in your preferred external IDE (like VS Code, Cursor, or PyCharm) with full LLM assistance. The moment you save, the component in Grasshopper updates its inputs, outputs, and geometry—without losing any connected wires.

## Features

- **Live Reload**: Watches your `.py` files and re-executes automatically when you save.
- **Dynamic Parameters**: Reads simple header comments in your code to automatically build Grasshopper inputs and outputs of the correct types.
- **Wire Preservation**: When your script updates, only new/changed parameters are altered; existing wires stay connected.
- **Integrated Logging**: Errors are displayed natively on the Grasshopper node and logged to a `gh_errors.log` file next to your script for easy debugging or AI context.
- **Built-in MCP Server**: 9 tools that let AI coding agents inspect the canvas, read/write scripts, check errors, and verify outputs — all without manual relay.

## Quick Start

### For users (writing Python scripts)
1. Install the plugin — see `setup/SETUP.md` for detailed instructions
2. Read `scripting/FIRST_PROMPT.md` — this is the entry point for understanding the scripting workflow
3. Copy `scripting/SCRIPT_TEMPLATE.py` as your starting point

### For developers (modifying the C# plugin)
1. Read `src/.context/HANDOFF.md` — the full plugin development brief
2. Read `src/.context/DEVELOPMENT.md` — build, deploy, and debug workflow

## Installation

### Prerequisites
- Rhino 8 (Service Release 18 or newer, tested on SR28)
- Windows 10/11 or macOS

### Quick install
- **Windows:** Run `setup/install_windows.bat` from the repo root
- **macOS:** Run `setup/install_macos.sh` from the repo root

### Manual install
1. Copy `ScriptNodePlugin.gha` to your Grasshopper Libraries folder
   - **Windows:** `%APPDATA%\Grasshopper\Libraries\`
   - **macOS:** `~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper/Libraries/`
2. **Windows only:** Right-click the .gha → Properties → Unblock
3. Restart Rhino + Grasshopper

For full setup including MCP configuration for your code editor, see `setup/SETUP.md`.

## The ScriptNode Workflow

### Step 1: Place the Component
Find **ScriptNode** in the **Script** tab in Grasshopper and place it on the canvas.

### Step 2: Link a Script
Use a Panel to type the absolute path to your `.py` file. Connect it to `script_path`.

### Step 3: Define Parameters via Header
```python
#! python 3
# NODE_INPUTS: point:Point3d, size:float, items:list[Brep]
# NODE_OUTPUTS: result, log
```

Supported types: `Point3d`, `Vector3d`, `Plane`, `Line`, `Curve`, `Surface`, `Brep`, `Mesh`, `int`, `float`, `str`, `bool`, `color`, `geometry`. Use `list[Type]` for list access.

### Step 4: Write Logic, See Live Updates
```python
import Rhino.Geometry as rg

if size is None: size = 1.0

result = []
if items and point:
    for b in items:
        b.Translate(rg.Vector3d(point) * size)
        result.append(b)

log = f"Processed {len(result)} items."
```

Save the file — Grasshopper recomputes instantly. Wires stay connected.

### Step 5: AI Agent Integration (MCP)
The built-in MCP server auto-starts when the first ScriptNode is placed (green dot = active). It runs on `http://127.0.0.1:9876/mcp`.

## MCP Tools (9 total)

| Tool | Description |
|------|-------------|
| `get_canvas_info` | Lists all components on the GH canvas with types, connections, and error status |
| `get_component_outputs` | Reads actual output values from any component |
| `get_rhino_command_history` | Reads Rhino's command history (catches print output and warnings) |
| `clear_rhino_command_history` | Clears command history for isolated testing |
| `run_rhino_command` | Sends a command to the Rhino command line |
| `get_scriptnode_info` | Deep-dive into a ScriptNode: script path, parsed header, runtime messages |
| `get_script_source` | Reads the Python script file contents |
| `write_script_source` | Writes new code; non-empty overwrites need `confirm_overwrite: true` + timestamped `.bak` backup |
| `get_error_log` | Reads `gh_errors.log` for debugging |

### Editor Configuration

**Cursor** — `.cursor/mcp.json`:
```json
{ "mcpServers": { "scriptnode": { "url": "http://127.0.0.1:9876/mcp" } } }
```

**Antigravity** — Workspace MCP settings:
```json
{ "mcpServers": { "scriptnode": { "serverURL": "http://127.0.0.1:9876/mcp" } } }
```

**Claude Desktop** — `claude_desktop_config.json`:
```json
{ "mcpServers": { "scriptnode": { "url": "http://127.0.0.1:9876/mcp", "transport": "streamable-http" } } }
```

## Project Structure

```
GHP_DynamicNode/
├── src/                        # C# plugin source
│   ├── ScriptNodeComponent.cs  # Main component
│   ├── McpServer.cs            # MCP server core
│   ├── Tools/                  # MCP tool classes (3 files, 9 tools)
│   ├── bin/Release/net7.0/     # Build output
│   └── .context/               # Plugin dev bible
├── scripting/                  # User/agent scripting docs
│   ├── FIRST_PROMPT.md         # ← Start here for scripting
│   ├── HEADER_PROTOCOL.md      # Input/output syntax
│   ├── TYPE_LEXICON.md         # Complete type reference
│   ├── SCRIPT_TEMPLATE.py      # Copy-paste starter
│   ├── MCP_WORKFLOW.md         # MCP tools + debug protocol
│   ├── CHAINING.md             # How scripts connect in GH
│   ├── GOTCHAS.md              # Known issues + platform diffs
│   ├── ALGORITHMS.md           # Generative design systems ref
│   ├── PROJECT_CONTEXT.md      # Current design project brief
│   └── examples/               # Example scripts + reference material
├── setup/
│   ├── SETUP.md                # Full install + config guide
│   ├── install_windows.bat
│   └── install_macos.sh
└── README.md                   # ← You are here
```

---
*Created by Nick Gauci with AI assistance.*
