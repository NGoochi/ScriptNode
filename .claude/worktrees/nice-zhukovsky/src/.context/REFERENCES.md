# REFERENCES.md
### Reference Material

Include these in the coding agent workspace for context.

---

## Must Include

### 1. Script-Sync (IBOIS/EPFL)
- **Repo:** https://github.com/ibois-epfl/script-sync
- **What to grab:** The C# GH component source code (the Grasshopper side, not the VSCode extension). Specifically how it wraps FileSystemWatcher, handles file selection, and manages the connection between external file and GH component.
- **Why:** Closest prior art. Our plugin is essentially Script-Sync but with auto-pin generation.

### 2. HotLoader (Cam Newnham)
- **Repo:** https://github.com/camnewnham/HotLoader
- **What to grab:** The `IGH_VariableParameterComponent` implementation, how it handles hot-reloading compiled C# into GH components.
- **Why:** Best reference for dynamic param management in a GH plugin.

### 3. RhinoMCP IMPLEMENTATION.md
- **Already in project files**
- **Why:** Architecture reference for the Phase 2 MCP server. TCP socket pattern between C# plugin and external Python process.

### 4. McNeel Forum: Programmatic Script Components
- **URL:** https://discourse.mcneel.com/t/programmatically-creating-new-c-python-script-components/199692
- **Why:** Contains Ehsan Iran-Nejad's (GH scripting lead) SR18 API code examples. Shows `Python3Component.Create()`, `SetSource()`, `SetParametersFromScript()`, `ScriptVariableParam` usage.

### 5. McNeel Forum: Auto-Generating Python Pins
- **URL:** https://discourse.mcneel.com/t/auto-generating-python-pins/216514
- **Why:** Confirms SDK mode auto-pin behaviour and the GH_ScriptInstance pattern.

## Nice to Have

### 6. GH SDK API Docs
- https://developer.rhino3d.com/api/grasshopper/
- Specifically: `GH_Component`, `IGH_VariableParameterComponent`, `GH_ParamAccess`, `Param_GenericObject`

### 7. Rhino 8 Scripting Docs
- https://developer.rhino3d.com/guides/scripting/
- For Python execution API (`Rhino.Runtime.PythonScript`, `RhinoCode` namespace)

### 8. MCP Python SDK
- **Already in project files:** `modelcontextprotocolpython-sdk`
- For Phase 2 MCP server implementation

---

*Update as new references are discovered.*
