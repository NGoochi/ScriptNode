# MCP_WORKFLOW.md
### MCP Server Reference and Debug Protocol

---

## Server Details

- **URL:** `http://127.0.0.1:9876/mcp`
- **Transport:** Streamable HTTP
- **Auto-start:** The server boots when the first ScriptNode component is placed on the GH canvas. A green dot on the component confirms it's running.
- **Shared:** All ScriptNode components on the canvas share one server instance.
- **Tools:** 9 total, across 3 categories

---

## Tool Reference

### Category 1: Canvas Inspection

#### `get_canvas_info`
**Purpose:** Get a complete map of every component on the GH canvas — names, types, GUIDs, positions, wire connections, and runtime status.

**Parameters:** None

**When to use:**
- First thing in a session, to understand what's on the canvas
- After placing or connecting components, to verify the graph
- To find the GUID of a component you need to inspect further

**Returns:**
```json
{
  "success": true,
  "documentName": "my_definition.gh",
  "componentCount": 12,
  "components": [
    {
      "id": "a1b2c3d4-...",
      "name": "ScriptNode",
      "nickname": "ScriptNode",
      "type": "ScriptNodeComponent",
      "x": 450.0,
      "y": 200.0,
      "category": "Script",
      "subcategory": "ScriptNode",
      "runtimeMessageLevel": "Blank",
      "isScriptNode": true,
      "inputs": [
        {
          "name": "script_path",
          "nickname": "script_path",
          "type": "Text",
          "sourceCount": 1,
          "sources": [
            {
              "sourceComponentId": "e5f6g7h8-...",
              "sourceParamName": "Panel"
            }
          ]
        },
        {
          "name": "origin",
          "nickname": "origin",
          "type": "Point",
          "sourceCount": 1,
          "sources": [...]
        }
      ],
      "outputs": [
        {
          "name": "points",
          "nickname": "points",
          "type": "Generic Data",
          "recipientCount": 1,
          "recipients": [...]
        }
      ]
    }
  ]
}
```

**Key fields:**
- `isScriptNode: true` — identifies ScriptNode components vs native GH components
- `runtimeMessageLevel` — `"Blank"` (OK), `"Warning"` (orange), `"Error"` (red)
- `sources` / `recipients` — the wire connections, showing which components are connected to which

---

#### `get_component_outputs`
**Purpose:** Read the actual data values flowing through a component's output parameters.

**Parameters:**
- `component_id` (string, required) — GUID of the component

**When to use:**
- After a script runs successfully, to verify output values
- To check what data a native GH component is producing (for debugging wiring)
- To confirm that geometry objects are valid

**Returns:**
```json
{
  "success": true,
  "outputs": [
    {
      "name": "points",
      "nickname": "points",
      "type": "Generic Data",
      "dataCount": 10,
      "values": [
        "Point3d (0, 0, 0)",
        "Point3d (1, 0, 0)",
        "Point3d (2, 0, 0)"
      ],
      "truncated": false
    },
    {
      "name": "log",
      "nickname": "log",
      "type": "Generic Data",
      "dataCount": 1,
      "values": ["Generated 10 points at spacing 1.0"],
      "truncated": false
    }
  ]
}
```

**Notes:**
- Values are `.ToString()` representations — geometry appears as `"Point3d (x, y, z)"`, `"Curve (domain)"`, etc.
- Output is capped at 100 values per parameter. If `truncated: true`, there are more values than shown.
- Works on any component, not just ScriptNodes.

---

### Category 2: ScriptNode Inspection

#### `get_scriptnode_info`
**Purpose:** Deep-dive into a ScriptNode's state — script path, parsed header, runtime messages, file watcher status, and wire connections.

**Parameters:**
- `component_id` (string, optional) — GUID of a specific ScriptNode. Omit to get info for ALL ScriptNodes on the canvas.

**When to use:**
- To check if a script loaded correctly
- To see the parsed header (confirms the parser read your `NODE_INPUTS` / `NODE_OUTPUTS` correctly)
- To read runtime error/warning messages
- Without arguments, to get an overview of all ScriptNodes

**Returns (single node):**
```json
{
  "success": true,
  "node": {
    "id": "a1b2c3d4-...",
    "name": "ScriptNode",
    "scriptPath": "C:\\projects\\my_script.py",
    "hasFileWatcher": true,
    "headerInputs": [
      { "Name": "origin", "TypeHint": "Point3d", "IsList": false },
      { "Name": "count", "TypeHint": "int", "IsList": false },
      { "Name": "spacing", "TypeHint": "float", "IsList": false }
    ],
    "headerOutputs": ["points", "log"],
    "runtimeMessageLevel": "Error",
    "runtimeMessages": [
      { "level": "Error", "message": "Python error: name 'rg' is not defined" }
    ],
    "inputs": [...],
    "outputs": [...]
  }
}
```

**Key fields:**
- `headerInputs` / `headerOutputs` — what the parser extracted from the file header. If these don't match what you wrote, the header has a syntax error.
- `runtimeMessages` — the actual error/warning text. This is the same info that appears in the GH component balloon tooltip.
- `runtimeMessageLevel` — `"Blank"` (OK), `"Warning"`, `"Error"`

---

#### `get_script_source`
**Purpose:** Read the full contents of the Python file a ScriptNode is pointing at.

**Parameters:**
- `component_id` (string, required) — GUID of the ScriptNode

**When to use:**
- To review what code is currently loaded
- To check for syntax issues before making targeted edits
- When the user says "look at my script" and you need to see it

**Returns:**
```json
{
  "success": true,
  "path": "C:\\projects\\my_script.py",
  "lineCount": 42,
  "content": "#! python 3\n# NODE_INPUTS: origin:Point3d...\nimport Rhino.Geometry as rg\n..."
}
```

---

#### `write_script_source`
**Purpose:** Write new content to the Python file. The ScriptNode's FileSystemWatcher will detect the change and auto-reload — rebuilding parameters if the header changed, re-executing the script, and updating outputs.

**Parameters:**
- `component_id` (string, required) — GUID of the ScriptNode
- `content` (string, required) — the full Python source code to write

**When to use:**
- To create or overwrite a script directly from the agent
- For rapid iteration without needing the user to save manually
- When fixing a bug — read source, fix, write back

**Returns:**
```json
{
  "success": true,
  "path": "C:\\projects\\my_script.py",
  "bytesWritten": 847,
  "message": "Script written. ScriptNode will auto-reload via FileSystemWatcher."
}
```

**Caution:** This writes the entire file. There is no merge or diff. Always `get_script_source` first, modify, then `write_script_source` with the complete file.

---

#### `get_error_log`
**Purpose:** Read the `gh_errors.log` file that sits next to the Python script. Contains full tracebacks from the last execution failure.

**Parameters:**
- `component_id` (string, required) — GUID of the ScriptNode

**When to use:**
- When `get_scriptnode_info` shows `runtimeMessageLevel: "Error"`
- When the runtime message is truncated and you need the full traceback
- First step in any debug cycle

**Returns (error exists):**
```json
{
  "success": true,
  "exists": true,
  "path": "C:\\projects\\gh_errors.log",
  "content": "Traceback (most recent call last):\n  File \"my_script.py\", line 15\n    result = curve.Offset(plane, dist)\nAttributeError: 'NoneType' object has no attribute 'Offset'\n"
}
```

**Returns (no errors):**
```json
{
  "success": true,
  "exists": false,
  "message": "No error log found (no errors have occurred)."
}
```

---

### Category 3: Rhino Application

#### `get_rhino_command_history`
**Purpose:** Read the Rhino command history window. Catches `print()` output from Python scripts, Rhino warnings, plugin load messages, and anything else that goes to the command line.

**Parameters:** None

**When to use:**
- To check for Python `print()` output (which goes to Rhino, not GH)
- To see if the plugin loaded correctly
- To check for Rhino-level warnings that don't appear in GH
- After running a Rhino command via `run_rhino_command`

---

#### `clear_rhino_command_history`
**Purpose:** Clear the command history window. Use before a test to isolate that test's output.

**Parameters:** None

**When to use:**
- Before running a script or command when you want clean output
- When the history is full of noise from earlier operations

---

#### `run_rhino_command`
**Purpose:** Execute a Rhino command string as if typed into the command line.

**Parameters:**
- `command` (string, required) — the command to run, e.g. `"_Circle 0,0,0 10"` or `"_SelAll"` or `"-_Export \"C:/output.obj\""`

**When to use:**
- To execute Rhino commands that aren't available through Python scripting
- To test geometry operations directly
- To export files, change display modes, or perform viewport operations
- Prefix commands with `_` for language-independent execution

**Returns:**
```json
{
  "success": true,
  "commandRun": true
}
```

---

## The Debug Protocol

Follow this sequence when writing or fixing a ScriptNode script:

### Step 1 — Understand the canvas
```
Call: get_canvas_info
→ Identify all ScriptNodes and their GUIDs
→ Note wire connections and any existing errors
```

### Step 2 — Write or edit the script
```
Call: get_script_source (if editing existing)
→ Make changes
Call: write_script_source (to deploy)
→ OR: edit in external editor and save (FileSystemWatcher picks it up)
```

### Step 3 — Check status
```
Call: get_scriptnode_info (with the component GUID)
→ Check runtimeMessageLevel
```

### Step 4a — If Error
```
Call: get_error_log → read full traceback
Call: get_rhino_command_history → check for print output or Rhino-level errors
→ Fix the issue in the script
→ Return to Step 2
```

### Step 4b — If Warning
```
Call: get_scriptnode_info → read warning messages
→ Common warnings: missing inputs (safe to ignore if intentional), type mismatches
→ Fix or ignore as appropriate
```

### Step 4c — If OK (Blank)
```
Call: get_component_outputs → verify output values and counts
→ If outputs look wrong, review script logic
→ If outputs look correct, the script is working
```

### Step 5 — Verify wiring
```
Call: get_canvas_info → confirm wires are intact after edit
→ If wires dropped, check that you didn't rename a header parameter
```

### Step 6 — Clean up
```
Call: clear_rhino_command_history → clear noise for next iteration
```

---

## Tips for Agents

- **Always check status after writing.** The write → check → fix loop is the core workflow. Never assume a write was successful.
- **Use `get_error_log` before `get_scriptnode_info`** when debugging — the log has the full traceback, while the component message may be truncated.
- **`get_component_outputs` works on any component** — not just ScriptNodes. Use it to check what data native GH components are producing, to understand what's flowing into your script's inputs.
- **`run_rhino_command` is powerful but dangerous** — it can modify the Rhino document. Use it for inspection commands (`_SelAll`, `_What`) and viewport operations, not for destructive geometry operations unless the user explicitly asks.
- **Command history is noisy** — always `clear_rhino_command_history` before a test if you need to isolate output.

---

*End of MCP_WORKFLOW.md.*
