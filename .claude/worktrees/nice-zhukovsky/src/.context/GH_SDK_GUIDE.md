# GH_SDK_GUIDE.md
### Grasshopper SDK Patterns and Gotchas

---

## IGH_VariableParameterComponent

The interface that allows a component to have dynamic inputs/outputs. You MUST implement:

- `CanInsertParameter(GH_ParameterSide, int)` → bool
- `CanRemoveParameter(GH_ParameterSide, int)` → bool  
- `CreateParameter(GH_ParameterSide, int)` → IGH_Param
- `DestroyParameter(GH_ParameterSide, int)` → bool
- `VariableParameterMaintenance()` — called after param changes

## Param Registration Pattern

```csharp
// Create param
var param = new Param_GenericObject();  // or Param_ScriptVariable
param.Name = "my_input";
param.NickName = "my_input";
param.Description = "Description here";
param.Access = GH_ParamAccess.item;
param.Optional = true;
param.CreateAttributes();

// Register
Params.RegisterInputParam(param);
Params.OnParametersChanged();
VariableParameterMaintenance();
```

## Critical Rules

1. **Never modify params during SolveInstance.** Param changes must happen OUTSIDE the solution. Use `ScheduleSolution(delay, callback)` if you need to trigger a rebuild from inside a solve.

2. **ExpireSolution threading.** `ExpireSolution(true)` starts a new solution synchronously. From a background thread, use `RhinoApp.InvokeOnUiThread()` first.

3. **Param type matters in Rhino 8.** The new Script components use `RhinoCodePluginGH.Parameters.ScriptVariableParam`. Standard components use `Param_GenericObject`, `Param_Number`, `Param_Point`, etc. from `Grasshopper.Kernel.Parameters`. For ScriptNode, use the standard GH param types — we are NOT a script component, we are a regular component with variable params.

4. **Serialization.** Variable params must survive document save/load. Override `Write` and `Read` on the component to persist the current param configuration and script path.

5. **ScheduleSolution pattern:**
```csharp
var doc = OnPingDocument();
if (doc != null)
{
    doc.ScheduleSolution(100, d => {
        // Safe to modify params here
        RebuildParameters();
        ExpireSolution(false);
    });
}
```

## Known macOS Issues

- FileSystemWatcher fires duplicate events
- FileSystemWatcher may miss rapid successive saves
- Font rendering differences in component UI
- Path separators handled by Path.Combine but watch for hardcoded strings

---

*Expand with discovered gotchas during development.*
