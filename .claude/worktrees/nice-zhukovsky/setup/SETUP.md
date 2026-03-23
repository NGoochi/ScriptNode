# SETUP.md
### Installation and Configuration Guide
**For:** An LLM agent setting up ScriptNode on a user's machine, OR a human following along.
**Read fully before executing any steps.**

---

## Prerequisites

- **Rhino 8** (Service Release 18 or newer, SR28 tested)
- **Operating System:** Windows 10/11 or macOS (Apple Silicon or Intel)
- **A code editor with MCP support** (Cursor, Antigravity, or Claude Desktop)

---

## Step 1: Install the Plugin

### Detect OS
Determine whether the machine is Windows or macOS. This affects file paths.

### Copy the .gha file

**Windows:**
```powershell
# The GH components folder:
$ghLibraries = "$env:APPDATA\Grasshopper\Libraries"

# Copy the plugin (adjust source path to wherever the repo is cloned)
Copy-Item "path\to\repo\src\bin\Release\net7.0\ScriptNodePlugin.gha" -Destination $ghLibraries
```

**macOS:**
```bash
# The GH components folder:
GH_LIBRARIES="$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper/Libraries"

# Copy the plugin
cp "path/to/repo/src/bin/Release/net7.0/ScriptNodePlugin.gha" "$GH_LIBRARIES/"
```

If a pre-built `.gha` is provided in the repo's releases or root folder, use that instead of building from source.

### Windows only: Unblock the file
Right-click the `.gha` file in the Libraries folder → Properties → if there's an "Unblock" checkbox under Security, check it → Apply. Without this, Windows blocks downloaded DLLs and GH won't load the plugin.

### Verify
1. Open Rhino 8
2. Open Grasshopper (type `Grasshopper` in the Rhino command line)
3. In the GH component toolbar, look for the **Script** tab → **ScriptNode** subcategory
4. If ScriptNode appears as a component, the plugin is installed correctly
5. If not: check that the `.gha` is in the correct Libraries folder and is unblocked

---

## Step 2: Configure the MCP Server

The MCP server is built into the plugin and starts automatically when the first ScriptNode component is placed on the GH canvas. It runs on `http://127.0.0.1:9876/mcp` using streamable HTTP transport.

Configure your code editor to connect to it:

### Cursor

Create or edit `.cursor/mcp.json` in your project root:
```json
{
  "mcpServers": {
    "scriptnode": {
      "url": "http://127.0.0.1:9876/mcp"
    }
  }
}
```

### Antigravity

Add to your workspace MCP settings (Settings → MCP → Add Server):
```json
{
  "mcpServers": {
    "scriptnode": {
      "serverURL": "http://127.0.0.1:9876/mcp"
    }
  }
}
```

Note: Antigravity uses `serverURL` (not `url`).

### Claude Desktop

Edit `claude_desktop_config.json`:

**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
**macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`

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

Note: Claude Desktop requires the explicit `transport` field.

---

## Step 3: Verify MCP Connection

1. Ensure Rhino + Grasshopper are open
2. Place a ScriptNode component on the GH canvas
3. Confirm the green dot appears on the component (MCP server is running)
4. In your code editor, ask the agent to call `get_canvas_info`
5. If the agent receives a JSON response listing the components on the canvas, the MCP connection is working

If the connection fails:
- Check that Grasshopper is open and a ScriptNode is on the canvas
- Check that no firewall is blocking localhost port 9876
- Check that the MCP config JSON is valid (no trailing commas, correct key names)
- Restart the code editor after adding the MCP config

---

## Step 4: Set Up the Scripting Workspace

The `scripting/` folder in this repo contains everything an agent needs to write ScriptNode Python scripts. Point your code editor's workspace at the repo root so the agent can read:

- `scripting/FIRST_PROMPT.md` — the master prompt (read this first in every new session)
- `scripting/HEADER_PROTOCOL.md` — input/output syntax
- `scripting/TYPE_LEXICON.md` — complete type reference
- `scripting/SCRIPT_TEMPLATE.py` — copy-paste starter
- `scripting/MCP_WORKFLOW.md` — MCP tools and debug protocol
- `scripting/CHAINING.md` — how scripts connect in GH
- `scripting/GOTCHAS.md` — known issues and platform differences
- `scripting/ALGORITHMS.md` — generative design system reference
- `scripting/PROJECT_CONTEXT.md` — current design project brief
- `scripting/examples/` — working example scripts and user reference material

Tell the agent: "Read `scripting/FIRST_PROMPT.md` and follow the reading order before doing anything."

---

## Step 5: Test with a Simple Script

1. Create a file called `test_add.py` anywhere on your machine (e.g., Desktop)
2. Paste this content:
```python
#! python 3
# NODE_INPUTS: a:float, b:float
# NODE_OUTPUTS: result
if a is None: a = 0.0
if b is None: b = 0.0
result = a + b
```
3. In Grasshopper, drop a **Panel** component and type the full path to `test_add.py`
4. Connect the Panel to the ScriptNode's `script_path` input
5. The ScriptNode should sprout two inputs (`a`, `b`) and one output (`result`)
6. Connect Number Sliders to `a` and `b`
7. The output should show the sum
8. Edit the .py file to change `result = a + b` to `result = a * b`, save
9. The output should update automatically to show the product

If this works, the full pipeline is confirmed: file watching → header parsing → execution → output.

---

## Troubleshooting

| Problem | Solution |
|---|---|
| ScriptNode not in toolbar | .gha not in Libraries folder, or file is blocked (Windows). See Step 1. |
| No green dot on component | MCP server didn't start. Check Rhino command history for errors. |
| Agent can't connect to MCP | Check MCP config JSON syntax. Check port 9876 isn't blocked. |
| Node doesn't update on save | macOS FileSystemWatcher lag. Right-click → Reload Script. |
| Wires disconnect on edit | You renamed a parameter in the header. Keep names stable. See GOTCHAS.md. |
| Script errors with IronPython syntax | Missing `#! python 3` shebang. See GOTCHAS.md. |

---

## For Plugin Developers

If you need to modify or rebuild the C# plugin itself, see `src/.context/DEVELOPMENT.md` for the build/deploy workflow, and `src/.context/HANDOFF.md` for the full plugin architecture brief.

---

*End of SETUP.md.*
