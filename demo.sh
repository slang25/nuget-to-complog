#!/bin/bash
set -e

# ============================================================
# nuget-patch demo: eject → edit → diff → apply
# Creates a console app, patches a NuGet dependency, and
# shows the patched behavior at runtime.
# ============================================================

TOOL_DIR="$(cd "$(dirname "$0")" && pwd)"
N2CL="dotnet run --project $TOOL_DIR/src/NuGetToCompLog --"

DEMO_DIR="$(pwd)/nuget-patch-demo"

echo "==========================================="
echo " nuget-patch demo"
echo "==========================================="
echo ""

# Clean up any previous run
if [ -d "$DEMO_DIR" ]; then
    echo "Removing previous demo directory..."
    rm -rf "$DEMO_DIR"
fi

mkdir -p "$DEMO_DIR"
cd "$DEMO_DIR"

# ── Step 1: Create a console app that uses Newtonsoft.Json ────
echo "▶ Step 1: Creating console app with Newtonsoft.Json dependency..."
echo ""

dotnet new console -n DemoApp -o DemoApp --no-restore
cd DemoApp
dotnet add package Newtonsoft.Json --version 13.0.3
dotnet restore

cat > Program.cs << 'EOF'
using Newtonsoft.Json;

var obj = new { name = "world", patched = false };
var json = JsonConvert.SerializeObject(obj);

Console.WriteLine("=== nuget-patch demo ===");
Console.WriteLine();
Console.WriteLine(json);
Console.WriteLine();
EOF

echo "   Created DemoApp with Newtonsoft.Json 13.0.3"
echo ""

# ── Step 2: Run the app BEFORE patching ───────────────────────
echo "▶ Step 2: Running app with ORIGINAL Newtonsoft.Json..."
echo ""

dotnet run
echo ""

# ── Step 3: Eject the package ─────────────────────────────────
# Eject to a sibling directory (not inside DemoApp) to avoid
# SDK-style projects globbing the ejected .cs files.
echo "▶ Step 3: Ejecting Newtonsoft.Json 13.0.3..."
echo "   (downloads package, recovers source from PDB/SourceLink)"
echo ""

cd "$DEMO_DIR"
$N2CL eject Newtonsoft.Json 13.0.3

PATCH_DIR="$DEMO_DIR/patches/Newtonsoft.Json+13.0.3"
echo ""
echo "   Source files: $(find "$PATCH_DIR/src" -name '*.cs' 2>/dev/null | wc -l | tr -d ' ')"
echo ""

# ── Step 4: Make a behavioral change ─────────────────────────
echo "▶ Step 4: Patching JsonConvert.cs..."
echo ""

TARGET_FILE=$(find "$PATCH_DIR/src" -name "JsonConvert.cs" 2>/dev/null | head -1)

if [ -z "$TARGET_FILE" ]; then
    echo "ERROR: Could not find JsonConvert.cs!"
    echo "   Available files:"
    find "$PATCH_DIR/src" -name "*.cs" | head -10
    exit 1
fi

RELATIVE=$(basename "$TARGET_FILE")
echo "   Editing: $RELATIVE"

# Patch the simplest SerializeObject overload to prepend "/* [PATCHED] */ "
sed -i.bak 's/return SerializeObject(value, null, (JsonSerializerSettings?)null);/return "\/* [PATCHED] *\/ " + SerializeObject(value, null, (JsonSerializerSettings?)null);/' "$TARGET_FILE"
rm -f "${TARGET_FILE}.bak"

if diff -q "$PATCH_DIR/.original/$RELATIVE" "$TARGET_FILE" > /dev/null 2>&1; then
    echo "   WARNING: Could not patch JsonConvert.cs"
    echo "   Showing the target method:"
    grep -n -A2 'public static string SerializeObject(object? value)' "$TARGET_FILE" | head -5 | sed 's/^/   /'
    exit 1
fi

echo ""
echo "   ── Diff preview ──"
diff "$PATCH_DIR/.original/$RELATIVE" "$TARGET_FILE" | head -20 | sed 's/^/   │ /' || true
echo ""

# ── Step 5: Create a patch file ───────────────────────────────
echo "▶ Step 5: Creating patch file..."
echo ""

$N2CL diff Newtonsoft.Json

PATCH_FILE="$DEMO_DIR/patches/Newtonsoft.Json+13.0.3.patch"

if [ -f "$PATCH_FILE" ]; then
    echo ""
    echo "   ── Patch file ──"
    cat "$PATCH_FILE" | sed 's/^/   │ /'
    echo ""
else
    echo "   WARNING: Patch file was not created"
fi

# ── Step 6: Apply and rebuild ─────────────────────────────────
echo "▶ Step 6: Applying patch and rebuilding assembly..."
echo ""

$N2CL apply Newtonsoft.Json

REBUILT_DLL=$(find "$PATCH_DIR/bin" -name "*.dll" 2>/dev/null | head -1)

echo ""
if [ -n "$REBUILT_DLL" ]; then
    echo "   Rebuilt assembly: $(basename "$REBUILT_DLL")"
    echo "   Size: $(wc -c < "$REBUILT_DLL" | tr -d ' ') bytes"
    echo ""

    # ── Step 7: Swap the DLL and re-run ───────────────────────
    echo "▶ Step 7: Swapping DLL and running app with PATCHED Newtonsoft.Json..."
    echo ""

    # Build the app so we have the output directory populated
    cd "$DEMO_DIR/DemoApp"
    dotnet build 2>/dev/null || true
    ORIGINAL_DLL=$(find "$DEMO_DIR/DemoApp/bin" -name "Newtonsoft.Json.dll" | head -1)

    if [ -n "$ORIGINAL_DLL" ]; then
        cp "$REBUILT_DLL" "$ORIGINAL_DLL"
        dotnet run --no-build
        echo ""
    else
        echo "   Could not find Newtonsoft.Json.dll in build output to swap"
    fi

    echo "==========================================="
    echo " DONE!"
    echo "==========================================="
    echo ""
    echo " Compare the two runs above to see the patch in action."
    echo ""
    echo " Workflow:"
    echo "   1. Created a console app using Newtonsoft.Json"
    echo "   2. Ran it → original JSON output"
    echo "   3. Ejected Newtonsoft.Json source from NuGet package"
    echo "   4. Patched SerializeObject() to prepend [PATCHED]"
    echo "   5. Created a .patch file"
    echo "   6. Rebuilt the assembly from patched source"
    echo "   7. Swapped the DLL and re-ran → patched output"
else
    echo "==========================================="
    echo " PARTIAL SUCCESS"
    echo "==========================================="
    echo ""
    echo " The eject and diff steps worked correctly."
    echo " The rebuild step may have failed due to"
    echo " missing references or compiler differences."
    echo " Check output above for details."
fi
