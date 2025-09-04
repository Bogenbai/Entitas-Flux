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

# copy only primary project DLLs (exclude tests and editor tooling)
# heuristic: dll name == project file name (or AssemblyName if set)
while IFS= read -r csproj; do
  name_from_file="$(basename "${csproj%.csproj}")"
  dll="$name_from_file.dll"

  if [[ -f "$TMP_OUT/$dll" ]]; then
    cp "$TMP_OUT/$dll" "$ARTIFACTS_DIR/"
  else
    # try AssemblyName if it differs from project file name
    asm_name="$(dotnet msbuild "$csproj" -nologo -getProperty:AssemblyName 2>/dev/null | tail -n1 || true)"
    if [[ -n "${asm_name:-}" && -f "$TMP_OUT/$asm_name.dll" ]]; then
      cp "$TMP_OUT/$asm_name.dll" "$ARTIFACTS_DIR/"
    fi
  fi
done < <(
  find "$SCRIPT_DIR" -type f -name '*.csproj' \
    -not -iname '*test*' \
    -not -iname '*tests*' \
    -not -iname '*unity.editor*'
)

# optional: remove temp build folder
rm -rf "$TMP_OUT"

echo "Artifacts:"
ls -1 "$ARTIFACTS_DIR" || true
echo "Done → $ARTIFACTS_DIR"
