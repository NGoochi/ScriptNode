# ScriptNodePlugin — Development Guide

## Build & Deploy

### The #1 Rule: ALWAYS verify deployment

```powershell
# Build (outputs to bin\Release\net7.0\ AND copies to GH Libraries)
dotnet build -c Release

# Verify timestamps match (if they don't, the plugin isn't deployed!)
$lib = "$env:APPDATA\Grasshopper\Libraries\ScriptNodePlugin.gha"
$bin = "bin\Release\net7.0\ScriptNodePlugin.gha"
Write-Host "Libraries: $(Get-Item $lib | Select -Expand LastWriteTime)"
Write-Host "Build:     $(Get-Item $bin | Select -Expand LastWriteTime)"
```

> **CRITICAL**: The `.csproj` contains a post-build copy step that copies the `.gha` 
> to `%APPDATA%\Grasshopper\Libraries\`. If this fails silently (e.g. Rhino has the 
> file locked), you will be running an OLD version of the plugin with ZERO indication 
> that your changes aren't loaded. **Always close Rhino before building.**

### Build workflow
1. **Close Rhino** (GH locks the .gha at startup)
2. `dotnet build -c Release` (from the `src/` directory)
3. Check output says "→ Copied ScriptNodePlugin.gha to Grasshopper Libraries"
4. Start Rhino + Grasshopper

### Verifying your code is loaded
Add a temporary `RhinoApp.WriteLine("TEST123");` at the top of `SolveInstance`.
If you see "TEST123" in the Rhino command line, your code is running.
If you see nothing, **your build isn't deployed**.

```csharp
// Requires: using Rhino;
RhinoApp.WriteLine("your message here");  // Shows in Rhino command line
```

## Architecture: Wire Preservation

### The Problem
When ScriptNode detects a header change (e.g. new parameter added), it must 
rebuild its dynamic input/output parameters. `UnregisterInputParameter()` destroys 
connection records on the param objects. Without intervention, all wires are lost.

### The Solution: "Hops Pattern"
Named after McNeel's Hops component which uses the same approach. Implemented in 
`RebuildParameters()` in `ScriptNodeComponent.cs`.

**Four steps, all inside the `ScheduleSolution(5ms)` callback:**

1. **Save live `IGH_Param` references** — `new List<IGH_Param>(p.Sources)` keyed
   by `NickName`. The upstream/downstream param objects survive because they belong
   to OTHER components — only our params get destroyed.

2. **Unregister all dynamic params** — full teardown, clean slate.

3. **Register new params** — from the parsed `# NODE_INPUTS:` / `# NODE_OUTPUTS:` 
   header. Call `Params.OnParametersChanged()` + `VariableParameterMaintenance()`.

4. **Restore connections by name match** — 
   - Inputs: `newParam.AddSource(savedUpstreamParam)` 
   - Outputs: `downstreamRecipient.AddSource(newOutputParam)` (reversed direction!)

### Key GH API Rules
- **Never modify topology during a running solution** — no `AddSource`, `RegisterInputParam`,
  or `UnregisterInputParameter` inside `SolveInstance`. Use `ScheduleSolution(5ms)`.
- **`new List<IGH_Param>(p.Sources)` not `p.Sources`** — must snapshot the collection 
  before unregistering (the original list becomes invalid).
- **Output wires are reversed** — `recipient.AddSource(outputParam)`, NOT 
  `outputParam.AddRecipient(recipient)`. GH wiring is always from the receiver's 
  perspective.
- **`Params.OnParametersChanged()` is mandatory** after any param list modification.

### Timing
```
FileSystemWatcher fires → 150ms debounce → ExpireSolution(true) 
→ SolveInstance runs → detects header change → ScheduleSolution(5ms) → return
→ 5ms later: callback runs (between solutions — topology safe)
  → save wires → unregister → register → OnParametersChanged → restore wires
  → ExpireSolution(false) → new solution starts → SolveInstance runs normally
```

## Debugging Tips

### No output from RhinoApp.WriteLine?
**Your plugin isn't loaded.** Check timestamps as shown above.

### `Debug.WriteLine` vs `RhinoApp.WriteLine`
- `Debug.WriteLine` → Visual Studio Output window only (invisible without VS)
- `RhinoApp.WriteLine` → Rhino command line (visible to user)

### Wires disappearing?
1. Check that `RebuildParameters()` is being called (add temp logging)
2. Check that `savedInputSources` has entries (sources are captured before unregister)
3. Check that `AddSource` is called after `OnParametersChanged()` (not before)
4. Check that the callback runs between solutions (not inside SolveInstance)
