# UnifiedNode — Project Brief
**Component:** `ScriptNode` (absorbs DataNode)  
**Author:** Nick Gauci / AIAA Studio  
**Date:** 2026-03-22  
**Status:** Pre-build — awaiting implementation

---

## 1. What This Is

A single Grasshopper component that replaces both `ScriptNode` and `DataNode`. It runs external Python 3 scripts with dynamic pins (inherited from ScriptNode), and also allows manual data entry for any GH data type on any parameter (inherited from DataNode). The visual editor is a browser-based HTML UI served over localhost — no Eto widget tree, no rebuild cost. A WebSocket channel handles live updates; it is off by default and togglable per node.

The MCP server remains embedded and boots automatically on component placement.

---

## 2. Core Modes

The component operates in two non-exclusive modes. Both can be active simultaneously.

### 2a. Script Mode
Unchanged from existing ScriptNode:
- Loads an external `.py` file
- Reads header comments to build input/output pins dynamically
- Watches file for changes, re-executes on save
- Pin types, names, and access are derived from the header

### 2b. Manual Data Mode
Replaces DataNode:
- Any input pin can have a manual value set via the browser UI
- Manual values persist in the component's serialised state (GH file save/load)
- When a wire is connected to a pin, the wire takes precedence — manual value is greyed out in UI but preserved
- When the wire is disconnected, the manual value is restored
- Manual values are available to the script as normal inputs

These two modes interact cleanly: a script defines the pins; the user either wires them or fills them manually.

---

## 3. Supported Manual Data Types

The browser UI must provide appropriate input controls for each GH-native type. Minimum set:

| GH Type | UI Control | Notes |
|---|---|---|
| `float` / `double` | Number input + optional slider | Slider range from param metadata |
| `int` | Integer input + optional slider | |
| `string` | Text input | Multiline option |
| `bool` | Toggle / checkbox | |
| `Point3d` | Three number inputs (x, y, z) | |
| `Vector3d` | Three number inputs (x, y, z) | |
| `Colour` | Colour picker | Hex + RGBA |
| `Domain` | Two number inputs (min, max) | |
| `generic` | Raw text fallback | For unsupported types — passed as string, script handles parsing |

The type is determined by the header declaration on the pin. If the type is not in the above list, fall back to raw text input and pass the string to the script.

---

## 4. Python Header Protocol — Extended

### 4a. Current format (preserved)
```python
# inputs: x:float, y:float
# outputs: result:float
```

### 4b. New: metadata row per param
Each param declaration gets an optional `|` suffix containing a description string. This string is displayed in the browser UI as a tooltip / subtext. It should describe intended use and data range.

```python
# inputs: x:float | "X coordinate of point. Range: 0.0 – 100.0 (model units)"
#         y:float | "Y coordinate of point. Range: 0.0 – 100.0 (model units)"
#         count:int | "Number of members to generate. Range: 1 – 500. High values slow solve."
#         label:string | "Text label for this item. Used in bake layer naming."
# outputs: members:geometry | "Generated timber member meshes. One per count value."
#          lengths:float | "Member lengths in model units. Parallel list to members output."
```

**Parsing rules:**
- `|` is the delimiter between type declaration and metadata
- Metadata is a quoted string — quotes are stripped before display
- Metadata is optional — existing scripts without `|` continue to work unchanged
- If slider range is parseable from the metadata string (e.g. `Range: 0.0 – 100.0`), the UI attempts to set slider min/max automatically. This is best-effort, not required
- Metadata is stored per-param in the component's serialised state alongside manual values

**Header parsing is the only place metadata is defined.** Do not add a separate metadata block or secondary comment format. Keep everything in the param declaration line.

---

## 5. Browser UI Architecture

### 5a. Server
The existing MCP HTTP server (already embedded in ScriptNode) is extended:
- Add a WebSocket upgrade handler at `/ws/node/{guid}`
- Add a GET endpoint at `/editor/node/{guid}` that serves the HTML editor
- The HTML is embedded as a C# resource string — no external files required

One server instance per Rhino session. All node instances share the server; each has its own WebSocket connection identified by component GUID.

### 5b. Opening the editor
Right-click menu → "Edit Node…" calls `Process.Start("http://localhost:{PORT}/editor/node/{guid}")`. Opens in system default browser. No Eto window.

The editor can be open for multiple nodes simultaneously (separate tabs). Each tab maintains its own WebSocket connection.

### 5c. WebSocket message protocol
All messages are JSON. Two message types:

**Server → Browser (state push):**
```json
{
  "type": "state",
  "guid": "...",
  "params": [
    {
      "name": "x",
      "nickName": "x",
      "direction": "input",
      "typeHint": "float",
      "meta": "X coordinate. Range: 0–100",
      "manualValue": 42.0,
      "isWired": false,
      "liveEnabled": false
    }
  ],
  "scriptPath": "path/to/script.py",
  "liveMode": false
}
```

**Browser → Server (value update):**
```json
{
  "type": "update",
  "guid": "...",
  "param": "x",
  "value": 57.5
}
```

**Browser → Server (apply batch):**
```json
{
  "type": "apply",
  "guid": "...",
  "values": { "x": 57.5, "y": 12.0 }
}
```

**Browser → Server (toggle live mode):**
```json
{
  "type": "setLive",
  "guid": "...",
  "enabled": true
}
```

### 5d. Live mode behaviour
- **Off (default):** Value changes in the browser do not trigger GH recompute. A visible "Apply" button commits all pending changes in one batch. The GH node message shows `(pending changes)` when there are uncommitted values.
- **On:** Every value change fires an `update` message immediately. The server receives it, updates the param value, and calls `RequestRecompute()`. A throttle of ~50ms is applied server-side to avoid flooding GH.
- Live mode state is serialised with the component (persists on file save/load)
- Live mode is toggled via a switch in the browser UI header (not buried in menus)

---

## 6. Component State — Serialisation

The component writes to GH's native `GH_IWriter` / `GH_IReader` (same as current ScriptNode and DataNode). Serialised state includes:

- Script file path
- Per-param manual values (keyed by param name)
- Per-param metadata strings (keyed by param name)
- Live mode enabled flag
- Browser UI window size/position (optional, best-effort)

Manual values are stored as strings in serialisation and cast to the correct type on read, using the typeHint as the casting guide.

---

## 7. MCP Tools — Updates

The existing 9 MCP tools remain. Two additions:

**`get_node_state`** — returns the full serialised state for a node by GUID, including manual values and metadata. Allows an LLM agent to read what's currently set in the UI without opening it.

**`set_param_value`** — sets a manual value on a named param for a given node GUID, then triggers recompute. Allows an LLM agent to drive parameter values programmatically.

---

## 8. What Is Not Changing

- The `IGH_VariableParameterComponent` implementation — pin rebuild logic stays identical
- The `ScheduleSolution` / `_needsRebuild` flag pattern — no changes
- Wire preservation on rebuild (snapshot/restore by name)
- The `.context/` bible system and `state.json`
- `SCRIPT_MAP.md` as the only volatile document
- Cross-platform requirement: Windows 11 + macOS must both work
- Eto Forms still used for everything except the data editor (file dialogs, any future panels)
- No HTML WebView embedded in Eto — browser is always the system browser

---

## 9. What Is Being Removed

- `DataNodeComponent.cs` — fully absorbed into the unified component
- `DataNodeEditor.cs` — replaced by browser UI
- `DataNodeAttributes.cs` — rendering rolled into `ScriptNodeAttributes.cs`
- `DataNodeSchema` class — replaced by per-param manual value storage on the component itself

The DataNode concept is preserved; the DataNode as a separate component is retired.

---

## 10. Files to Produce

| File | Description |
|---|---|
| `UnifiedNodeComponent.cs` | Main component — merges ScriptNode + DataNode |
| `UnifiedNodeAttributes.cs` | GH canvas rendering |
| `WebSocketServer.cs` | WS upgrade handler on existing HTTP server |
| `EditorHtml.cs` | Embedded HTML string (the full browser UI) |
| `ManualValueStore.cs` | Typed value storage + serialisation helpers |
| `McpTools.cs` | Updated MCP tool implementations (add 2 new tools) |

Header parser update is in the existing `ScriptHeaderParser.cs` (or equivalent) — add `|` metadata parsing.

---

## 11. Open Questions (resolve before build)

1. **Naming** — keep calling it `ScriptNode` in GH UI, or rename to `UnifiedNode` / something else?
2. **Component GUID** — new GUID breaks existing GH files using the old ScriptNode. Acceptable? Or provide migration path?
3. **Live mode throttle** — 50ms server-side. Is 20ms (50fps) preferable given your use case?
4. **Metadata parsing of ranges** — attempt to auto-set slider min/max from the description string, or always require explicit `min:` / `max:` keys in the header?
5. **Port** — MCP server currently on what port? WebSocket shares same port via upgrade, or separate?

---

*End of brief.*
