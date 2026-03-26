# CONVENTIONS.md
### C# and Plugin Coding Conventions

---

## Naming
- Files: `PascalCase.cs`
- Classes: `PascalCase`
- Methods: `PascalCase`
- Private fields: `_camelCase`
- Constants: `UPPER_SNAKE`
- Namespace: `ScriptNodePlugin`

## File Structure
- One class per file
- File name matches class name

## Cross-Platform
- Never use `\\` in paths — always `Path.Combine()`
- Never assume Windows line endings — handle `\r\n` and `\n`
- Test FileSystemWatcher behaviour on both platforms

## Threading
- FileSystemWatcher callbacks fire on background threads
- ALL Rhino/GH API calls must be on UI thread
- Use `RhinoApp.InvokeOnUiThread()` to marshal back
- Never call `ExpireSolution` from a background thread

## Error Handling
- Never let exceptions propagate to GH uncaught
- Use component `AddRuntimeMessage()` for user-visible errors
- Log full tracebacks to file for agent consumption

## Dependencies
- Target `net7.0`
- RhinoCommon and Grasshopper via NuGet
- No other external dependencies for Phase 1

---

*Expand this file as conventions are established during development.*
