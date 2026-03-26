# FIRST_PROMPT.md
### Scripting Guide for ScriptNode (Grasshopper Python Dynamic Node)
**For:** Any LLM coding agent writing Python scripts for ScriptNode
**Read this entire document before writing any code.**

---

## 0. What You Are Working With

**ScriptNode** is a custom Grasshopper plugin for Rhino 8. It loads external Python 3 (.py) files, dynamically builds its input/output parameters from header comments, watches for file changes, and re-executes on save. It includes a built-in MCP server (9 tools) that lets you — the coding agent — inspect the Grasshopper canvas, read errors, read/write script files, and verify outputs without the user needing to relay information manually.

You are writing Python scripts that run inside this node. Your scripts are plain Python 3 files with a specific header format. You do not need to understand C# or the plugin internals — just how to write correct `.py` files and how to use the MCP tools to test them.

---

## 1. Reading Order

Read these files in order before writing any scripts:

1. **This file** — overview, context, communication rules
2. **HEADER_PROTOCOL.md** — the exact syntax for declaring inputs and outputs
3. **TYPE_LEXICON.md** — every supported type, how it maps between Python / GH / RhinoCommon
4. **SCRIPT_TEMPLATE.py** — copy-paste starting point with annotations
5. **MCP_WORKFLOW.md** — the 9 MCP tools, debug loop protocol, example responses
6. **CHAINING.md** — how to write scripts that connect to each other in GH
7. **GOTCHAS.md** — platform differences, common failures, hard-won lessons
8. **ALGORITHMS.md** — reference implementations for generative design systems
9. **PROJECT_CONTEXT.md** — the current design project's brief, site, intent

If this is your first session, also scan `src/.context/` for the plugin development bible — particularly `DEVELOPMENT.md` (build/deploy workflow) and `ARCHITECTURE.md` (how the plugin works internally). You don't need this for scripting, but it helps if something goes wrong at the plugin level.

### The `examples/` folder

This folder ships with working example scripts and is also the designated drop zone for user-provided reference material — images, PDFs, text files, other scripts. The user may ask you to read files from here for context. Treat everything in `examples/` as potential input to your understanding of the project.

---

## 2. The Project

This tool was built for the **AIAA (AI-Accelerated Architects)** studio at RMIT Melbourne, led by Prof. Alisa Andrasek. The current design project is a mixed-use academic tower on a site north of Victoria Street, RMIT City Campus, Carlton. The building uses dense timber member aggregation driven by algorithmic systems (boids, voxel grids, attractors, Perlin noise, pathfinding).

However, ScriptNode is a general-purpose tool. The algorithms and project context files describe the current project's needs, but the scripting system works for any Grasshopper workflow. Update `ALGORITHMS.md` and `PROJECT_CONTEXT.md` as the project evolves — they are living documents.

---

## 3. Technical Environment

- **Rhino 8** (Service Release 18+, tested on SR28)
- **Python:** CPython 3.9 (Rhino 8's built-in runtime). No IronPython.
- **Platforms:** Windows 11 and macOS (Apple Silicon + Intel). Scripts must work on both.
- **File paths:** Always use `os.path.join()`. Never hardcode separators.
- **Imports available:** `Rhino`, `Rhino.Geometry`, `rhinoscriptsyntax as rs`, `Grasshopper`, `System`, `math`, `os`, `json`, `collections`, and most stdlib modules. No `numpy` or `scipy` unless separately installed.

---

## 4. How Communication Works

### With the user
- Ask questions before building anything significant.
- Flag when a change to one script will affect others in the chain.
- Be honest about uncertainty — this is experimental work.
- Keep responses tight. No padding.

### With Grasshopper (via MCP)
The ScriptNode MCP server runs on `http://127.0.0.1:9876/mcp` and auto-starts when the first ScriptNode is placed on the canvas. You have 9 tools — see `MCP_WORKFLOW.md` for the full protocol. The critical loop is:

1. Write or edit `.py` file (via `write_script_source` or external editor)
2. ScriptNode auto-reloads on save
3. Call `get_scriptnode_info` → check runtime status
4. If error → `get_error_log` → read traceback → fix → save → repeat
5. If OK → `get_component_outputs` → verify values

### Updating project state
- When you add or significantly change a script, update `PROJECT_CONTEXT.md` if the design intent has shifted.
- When you discover a new algorithm or pattern worth reusing, add it to `ALGORITHMS.md`.
- When you discover a new gotcha or platform difference, add it to `GOTCHAS.md`.
- These files are living documents. Keep them current.

---

## 5. What Success Looks Like

A script is successful when:
- It loads without errors (green status on the ScriptNode component)
- Its header correctly declares all inputs and outputs
- Connected wires survive script edits (header names are stable)
- Outputs are the correct type for downstream consumption
- It handles `None` inputs gracefully (unconnected params)
- It works on both Windows and macOS
- Another agent or user can read the header and understand what the script does

---

*End of FIRST_PROMPT.md.*
