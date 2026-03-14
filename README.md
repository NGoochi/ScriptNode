# ScriptNode (Grasshopper Python Dynamic Node)

A Grasshopper plugin for Rhino 8 that automatically syncs and rebuilds its parameters from an external Python script. 

Write complex Python logic in your preferred external IDE (like VS Code, Cursor, or PyCharm) with full LLM assistance. The moment you save, the component in Grasshopper updates its inputs, outputs, and geometry—without losing any connected wires.

## Features

- **Live Reload**: Watches your `.py` files and re-executes automatically when you save.
- **Dynamic Parameters**: Reads simple header comments in your code to automatically build Grasshopper inputs and outputs of the correct types.
- **Wire Preservation**: When your script updates, only new/changed parameters are altered; existing wires stay connected.
- **Integrated Logging**: Errors are displayed natively on the Grasshopper node and logged to a `gh_errors.log` file next to your script for easy debugging or AI context.

## Installation Process

### Prerequisites
- Rhino 8 (Service Release 18 or newer recommended, tested on SR28)
- Windows 10/11 or macOS

### Option A: Install from pre-built release (Recommended)
1. Download the latest `ScriptNode.gha` file from the repository releases page.
2. Open **Rhino 8** and launch **Grasshopper**.
3. In Grasshopper, go to **File > Special Folders > Components Folder**.
4. Drag and drop the `ScriptNode.gha` file into this folder.
5. **Important for Windows Users:** Right-click the dragged `ScriptNode.gha` file, select **Properties**, and if you see an "Unblock" checkbox at the bottom under the Security section, check it and click Apply.
6. Restart Rhino and Grasshopper.

### Option B: Build from source
This project targets `net7.0` (required for Rhino 8 and its new UI/API models).
1. Ensure the [.NET 7 SDK](https://dotnet.microsoft.com/download/dotnet/7.0) is installed.
2. Clone this repository and open a terminal.
3. Navigate to the `src` directory containing `ScriptNodePlugin.csproj`.
4. Run `dotnet build -c Release`.
5. The output `ScriptNodePlugin.gha` will be in `bin\Release\net7.0\`. 
6. Drag this `.gha` file into Grasshopper to install it.

## The ScriptNode Workflow

ScriptNode revolutionizes how you work with Python in Grasshopper, bridging the gap between node-based visual logic and external IDE power. Here is the step-by-step workflow:

### Step 1: Place the Component
Find the **ScriptNode** component (located under the **Script** tab in Grasshopper) and place it onto your canvas. 

### Step 2: Establish the Link
Use a standard Text Panel and type the **absolute path** to your empty or existing Python script (e.g., `C:\my_projects\my_script.py`). Connect this Text Panel into the component's `script_path` input.

### Step 3: Define Parameters via Header
Open your Python script in an external code editor (VS Code, Cursor, PyCharm, etc.). Structure the very top of your file to define the inputs and outputs your node needs:

```python
#! python 3
# NODE_INPUTS: point:Point3d, size:float, items:list[Brep]
# NODE_OUTPUTS: result, log
```

- **NODE_INPUTS**: A comma-separated list of `name:Type`. Use `list[Type]` to request List Access.
- **Supported Types**: `Point3d`, `Vector3d`, `Plane`, `Line`, `Curve`, `Surface`, `Brep`, `Mesh`, `int`, `float`, `str`, `bool`, `color`, `geometry`.
- **NODE_OUTPUTS**: A comma-separated list of output variable names.

*The moment you hit Save in your editor, your ScriptNode component will detect the changes, read the header, and magically sprout those precise input and output params in Grasshopper!*

### Step 4: Write Your Logic and See Live Updates
Your scripted inputs (from `NODE_INPUTS`) are immediately available as variables in your script environment. Do your Rhino/Grasshopper logic, then set variables matching the `NODE_OUTPUTS` names.

```python
import Rhino.Geometry as rg

# Defensively set defaults if inputs are empty
if size is None: 
    size = 1.0

# Process the inputs
result = []
if items and point:
    for b in items:
        # Move the breps
        b.Translate(rg.Vector3d(point) * size)
        result.append(b)

# Set the log output for visibility
log = f"Processed {len(result)} items."
```

When you save the `.py` file, Grasshopper instantly recomputes. Wires connected to your old inputs/outputs stay perfectly intact.

### Step 5: Leverage AI Assistants (MCP Integration)
ScriptNode includes a built-in MCP (Model Context Protocol) server. It runs locally and empowers external AI agents (like Claude Desktop, Cursor, or AI terminal tools) to inspect your GH canvas, read geometries, and edit your ScriptNode Python files directly.
- The server auto-starts when the first ScriptNode is placed.
- A **green dot** on the component means the server is actively running.
- It listens on `http://127.0.0.1:9876/mcp`.
- **Important:** Multiple ScriptNode components share a single MCP server.

## Agent Configuration (MCP Server)

### Available Tools

| Tool | Description |
|------|-------------|
| `get_canvas_info` | Lists all components on the GH canvas with types, connections, and error status |
| `get_scriptnode_info` | Deep-dive into a ScriptNode: script path, parsed header, runtime messages |
| `get_script_source` | Reads the Python script file contents |
| `write_script_source` | Writes new code to the Python file (triggers auto-reload) |
| `get_error_log` | Reads `gh_errors.log` for debugging |
| `get_component_outputs` | Reads actual output values from any component |


**Antigravity** — Add to your workspace MCP settings:
```json
{
  "mcpServers": {
    "scriptnode": {
      "serverURL": "http://127.0.0.1:9876/mcp"
    }
  }
}
```

**Claude Desktop** — Add to `claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "scriptnode": {
      "url": "http://127.0.0.1:9876/mcp",
      "transport": "streamable-http"
    }
  }
}
```

**Cursor** — Add to `.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "scriptnode": {
      "url": "http://127.0.0.1:9876/mcp"
    }
  }
}
```

---
*Created by Nick Gauci with AI Assistance.*
