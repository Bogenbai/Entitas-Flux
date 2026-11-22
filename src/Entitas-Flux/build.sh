#!/usr/bin/env bash
set -euo pipefail

# resolve absolute paths
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="$SCRIPT_DIR/Entitas.sln"
CONFIG="${1:-Release}"
# Version stamped into the built assemblies; CI passes the tag, locally it falls back
# to whatever Directory.Build.targets declares.
FLUX_VERSION="${FLUX_VERSION:-}"

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
if [[ -n "$FLUX_VERSION" ]]; then
  MSBUILD_PROPS+=("-p:FluxVersion=${FLUX_VERSION#v}")
fi

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

# Mapping of dll -> subfolder (relative to Artifacts/). DesperateDevs and Sherlog are
# third-party assemblies Entitas itself is built against; shipping without them left a
# fresh project unable to compile (the generated Feature class references
# DesperateDevs.Extensions) — existing projects only worked because they still had the
# copies from the original Entitas distribution. The same went for the editor:
# Entitas.Unity.Editor and Entitas.VisualDebugging.Unity.Editor reference
# DesperateDevs.Unity.Editor, and without it Unity refuses to load either of them.
while IFS='|' read -r dll dest
do
  copy_map_line "$dll" "$dest"
done <<'EOF'
Entitas.CodeGeneration.Attributes.dll|Assets/Entitas/Entitas
DesperateDevs.Caching.dll|Assets/Entitas/DesperateDevs
DesperateDevs.Extensions.dll|Assets/Entitas/DesperateDevs
DesperateDevs.Reflection.dll|Assets/Entitas/DesperateDevs
DesperateDevs.Serialization.dll|Assets/Entitas/DesperateDevs
Sherlog.dll|Assets/Entitas/DesperateDevs
DesperateDevs.Unity.Editor.dll|Assets/Entitas/DesperateDevs/Editor
Entitas.dll|Assets/Entitas/Entitas
Entitas.SourceGenerator.dll|Assets/Entitas/Entitas/Analyzers
Entitas.CodeFixes.dll|Assets/Entitas/Entitas/Analyzers
Entitas.Unity.dll|Assets/Entitas/Entitas
Entitas.VisualDebugging.Unity.dll|Assets/Entitas/Entitas
Entitas.Unity.Editor.dll|Assets/Entitas/Entitas/Editor
Entitas.VisualDebugging.Unity.Editor.dll|Assets/Entitas/Entitas/Editor
EOF

# The command-line tools the Unity editor runs out of process: entitas-gen (Tools >
# Entitas > Generate, and regeneration when a component changes) and entitas-rename
# (Tools > Entitas > Rename Component...). Framework-dependent and OS-independent — the
# editor starts them with the .NET runtime Unity itself ships. They go in a folder whose
# name ends in "~", which Unity never imports, so the Roslyn assemblies they carry cannot
# clash with anything in the project.
TOOLS_DIR="$ARTIFACTS_DIR/Assets/Entitas/Entitas/Tools~"
mkdir -p "$TOOLS_DIR"
for tool in entitas-gen entitas-rename; do
  for ext in dll deps.json runtimeconfig.json; do
    src="$TMP_OUT/$tool.$ext"
    if [[ -f "$src" ]]; then
      cp -f "$src" "$TOOLS_DIR/"
    else
      echo "error: expected tool file not found: $tool.$ext" >&2
      exit 1
    fi
  done
done
for dll in Entitas.SourceGenerator.dll Microsoft.CodeAnalysis.dll Microsoft.CodeAnalysis.CSharp.dll; do
  cp -f "$TMP_OUT/$dll" "$TOOLS_DIR/"
done

# optional: remove temp build folder
rm -rf "$TMP_OUT"

echo "Resulting layout (no DLLs in Artifacts/ root):"
find "$ARTIFACTS_DIR" -type d -print -o -type f -name '*.dll' -print | sed "s|$ARTIFACTS_DIR/||"
echo "Done → $ARTIFACTS_DIR"
echo
echo "NOTE: Assets/Entitas/Entitas/Analyzers/Entitas.SourceGenerator.dll is a Roslyn"
echo "incremental source generator. In the consuming Unity 2022.3+ project, its .meta"
echo "must carry the 'RoslynAnalyzer' asset label and have all platforms unchecked so"
echo "Unity feeds it to the compiler instead of loading it as a managed plugin."
