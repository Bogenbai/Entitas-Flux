#!/usr/bin/env bash
set -euo pipefail

# script directory (absolute)
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

# solution and output, anchored to the script directory
SOLUTION="$SCRIPT_DIR/Entitas.sln"
CONFIG="${1:-Release}"
ARTIFACTS_DIR="$SCRIPT_DIR/Artifacts"

mkdir -p "$ARTIFACTS_DIR"

dotnet clean "$SOLUTION" -c "$CONFIG" >/dev/null || true

MSBUILD_PROPS=(
  "-p:OutDir=$ARTIFACTS_DIR/"
  "-p:OutputPath=$ARTIFACTS_DIR/"
  "-p:AppendTargetFrameworkToOutputPath=false"
  "-p:AppendRuntimeIdentifierToOutputPath=false"
)

if ! dotnet build "$SOLUTION" -c "$CONFIG" "${MSBUILD_PROPS[@]}"; then
  echo "dotnet build failed, trying msbuild…"
  dotnet msbuild "$SOLUTION" -t:Build -p:Configuration="$CONFIG" "${MSBUILD_PROPS[@]}"
fi

echo "Done. You'll find your DLLs at $ARTIFACTS_DIR"
