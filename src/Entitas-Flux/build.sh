#!/usr/bin/env bash
set -euo pipefail

# resolve absolute paths
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="$SCRIPT_DIR/Entitas.sln"
CONFIG="${1:-Release}"

# temp build output and final artifacts
TMP_OUT="$SCRIPT_DIR/.buildout"
ARTIFACTS_DIR="$SCRIPT_DIR/Artifacts"

# clean outputs
rm -rf "$TMP_OUT" "$ARTIFACTS_DIR"
mkdir -p "$TMP_OUT" "$ARTIFACTS_DIR"

# build with a common out dir, but without PDBs
MSBUILD_PROPS=(
  "-p:OutDir=$TMP_OUT/"
  "-p:AppendTargetFrameworkToOutputPath=false"
  "-p:AppendRuntimeIdentifierToOutputPath=false"
  "-p:DebugSymbols=false"
  "-p:DebugType=None"
)

dotnet clean "$SOLUTION" -c "$CONFIG" >/dev/null || true
dotnet build "$SOLUTION" -c "$CONFIG" "${MSBUILD_PROPS[@]}"

# Copy only the requested DLLs into requested subfolders (no flat copies)
copy_map_line() {
  local dll="$1"
  local dest_rel="$2"
  [[ -z "${dll// }" || -z "${dest_rel// }" ]] && return 0

  local src="$TMP_OUT/$dll"
  if [[ -f "$src" ]]; then
    local dest_dir="$ARTIFACTS_DIR/$dest_rel"
    mkdir -p "$dest_dir"
    cp -f "$src" "$dest_dir/"
  else
    echo "WARN: expected DLL not found: $dll" >&2
  fi
}

# mapping of dll -> subfolder (relative to Artifacts/)
while IFS='|' read -r dll dest
do
  copy_map_line "$dll" "$dest"
done <<'EOF'
Entitas.CodeGeneration.Plugins.dll|Jenny/Jenny/Plugins/Entitas
Entitas.Roslyn.CodeGeneration.Plugins.dll|Jenny/Jenny/Plugins/Entitas
Entitas.VisualDebugging.CodeGeneration.Plugins.dll|Jenny/Jenny/Plugins/Entitas
Entitas.CodeGeneration.Attributes.dll|Assets/Entitas/Entitas
Entitas.dll|Assets/Entitas/Entitas
Entitas.Unity.dll|Assets/Entitas/Entitas
Entitas.VisualDebugging.Unity.dll|Assets/Entitas/Entitas
Entitas.Migration.dll|Assets/Entitas/Entitas/Editor
Entitas.Migration.Unity.Editor.dll|Assets/Entitas/Entitas/Editor
Entitas.Unity.Editor.dll|Assets/Entitas/Entitas/Editor
Entitas.VisualDebugging.Unity.Editor.dll|Assets/Entitas/Entitas/Editor
EOF

# optional: remove temp build folder
rm -rf "$TMP_OUT"

echo "Resulting layout (no DLLs in Artifacts/ root):"
find "$ARTIFACTS_DIR" -type d -print -o -type f -name '*.dll' -print | sed "s|$ARTIFACTS_DIR/||"
echo "Done → $ARTIFACTS_DIR"
