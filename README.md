# ScriptNode (Grasshopper Python Dynamic Node)

A Grasshopper plugin for Rhino 8 that automatically syncs and rebuilds its parameters from an external Python script. 

Write complex Python logic in your preferred external IDE (like VS Code, Cursor, or PyCharm) with full LLM assistance. The moment you save, the component in Grasshopper updates its inputs, outputs, and geometry—without losing any connected wires.

## Features

- **Live Reload**: Watches your `.py` files and re-executes automatically when you save.
- **Dynamic Parameters**: Reads simple header comments in your code to automatically build Grasshopper inputs and outputs of the correct types.
- **Wire Preservation**: When your script updates, only new/changed parameters are altered; existing wires stay connected.
- **Integrated Logging**: Errors are displayed natively on the Grasshopper node and logged to a `gh_errors.log` file next to your script for easy debugging or AI context.

## How It Works

### 1. Place the Component
Drop a **ScriptNode** component (found under the **Script** tab) onto your Grasshopper canvas. 

### 2. Point it to a Script
Connect a text panel with the absolute path to your Python script into the `script_path` input.

### 3. Write Your Header
In your external Python file, structure the top of the file like this:

```python
#! python 3
# NODE_INPUTS: point:Point3d, size:float, items:list[Brep]
# NODE_OUTPUTS: result, log
```

- **NODE_INPUTS**: A comma-separated list of `name:Type`. Use `list[Type]` to request List Access.
- Supported Types: `Point3d`, `Vector3d`, `Plane`, `Line`, `Curve`, `Surface`, `Brep`, `Mesh`, `int`, `float`, `str`, `bool`, `color`, `geometry`.
- **NODE_OUTPUTS**: A comma-separated list of output variable names.

When you hit Save in your editor, your ScriptNode component will magically sprouts those inputs and outputs!

### 4. Write Your Logic
The inputs defined in `NODE_INPUTS` are automatically injected as variables into your script. Your logic simply uses them, and then defines variables matching the names in `NODE_OUTPUTS`. 

```python
# Example logic using the inputs above:
import Rhino.Geometry as rg

# Defensively set defaults if inputs are empty
if size is None: 
    size = 1.0

# Process the inputs
result = []
if items and point:
    for b in items:
        b.Translate(rg.Vector3d(point) * size)
        result.append(b)
```

## Building

This project targets `net7.0` (required for Rhino 8 and its new UI/API models).

1. Ensure the .NET 7 SDK is installed.
2. In a terminal, navigate to the `src` directory containing `ScriptNodePlugin.csproj`.
3. Run `dotnet build -c Release`.
4. The output `.gha` will be in `bin\Release\net7.0\`. 
5. Drag this `.gha` into Grasshopper.

## Requirements

- Rhino 8 (Service Release 18 or newer recommended, tested on SR28)
- Windows 10/11 or macOS

---
*Created by Nick Gauci with AI Assistance.*
