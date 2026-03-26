#!/bin/bash
# ScriptNode Plugin Installer — macOS
# Run from the repo root directory

GH_LIBRARIES="$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper/Libraries"
GHA_SOURCE="src/bin/Release/net7.0/ScriptNodePlugin.gha"

echo "════════════════════════════════════════════"
echo " ScriptNode Plugin Installer (macOS)"
echo "════════════════════════════════════════════"

# Check if source exists
if [ ! -f "$GHA_SOURCE" ]; then
    echo "[ERROR] Could not find $GHA_SOURCE"
    echo "        Build the plugin first: cd src && dotnet build -c Release"
    echo "        Or use a pre-built .gha from the releases page."
    exit 1
fi

# Check if GH Libraries folder exists
if [ ! -d "$GH_LIBRARIES" ]; then
    echo "[ERROR] Grasshopper Libraries folder not found:"
    echo "        $GH_LIBRARIES"
    echo "        Is Rhino 8 installed? Open Grasshopper once to create the folder."
    exit 1
fi

# Check if Rhino is running
if pgrep -x "Rhinoceros" > /dev/null 2>&1; then
    echo "[WARNING] Rhino appears to be running."
    echo "          Close Rhino before installing, or the file may be locked."
    read -p "Continue anyway? (y/n): " CONTINUE
    if [ "$CONTINUE" != "y" ] && [ "$CONTINUE" != "Y" ]; then
        exit 0
    fi
fi

# Copy
echo "Copying ScriptNodePlugin.gha to Libraries folder..."
cp "$GHA_SOURCE" "$GH_LIBRARIES/ScriptNodePlugin.gha"

echo ""
echo "[DONE] Plugin installed."
echo "       Start Rhino + Grasshopper and look for ScriptNode in the Script tab."
