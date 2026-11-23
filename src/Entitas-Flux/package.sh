#!/usr/bin/env bash
# Assembles a Unity Package Manager package from the built Artifacts.
#
# Install for consumers is then a git URL instead of "download a zip, copy DLLs into
# Assets/, and remember to label two of them RoslynAnalyzer by hand".
#
# Usage: ./package.sh [version]   (default: 0.0.0-dev)
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
VERSION="${1:-0.0.0-dev}"
VERSION="${VERSION#v}"

ARTIFACTS="$SCRIPT_DIR/Artifacts/Assets/Entitas/Entitas"
PACKAGE="$SCRIPT_DIR/Artifacts/package"

if [[ ! -d "$ARTIFACTS" ]]; then
  echo "error: $ARTIFACTS not found — run ./build.sh first" >&2
  exit 1
fi

rm -rf "$PACKAGE"
mkdir -p "$PACKAGE/Runtime" "$PACKAGE/Editor" "$PACKAGE/Analyzers"

cp "$ARTIFACTS"/*.dll "$PACKAGE/Runtime/"

# Third-party assemblies Entitas is built against. Sherlog sits in its own folder in the
# archive (where the original Entitas distribution keeps it); in the package it stays in
# Runtime/DesperateDevs, so its path, and with it its guid, does not change.
mkdir -p "$PACKAGE/Runtime/DesperateDevs"
cp "$ARTIFACTS"/../DesperateDevs/*.dll "$ARTIFACTS"/../Sherlog/*.dll "$PACKAGE/Runtime/DesperateDevs/"
cp "$ARTIFACTS"/Editor/*.dll "$PACKAGE/Editor/"
# The hierarchy icons and the preferences header, found by their asset labels — which is
# why they come with their own .meta (unity_meta.py only swaps in the package's guid).
cp -R "$ARTIFACTS/Editor/Images" "$PACKAGE/Editor/Images"
# The editor assemblies' third-party dependency, editor-only like them.
if [[ -d "$ARTIFACTS/../DesperateDevs/Editor" ]]; then
  mkdir -p "$PACKAGE/Editor/DesperateDevs"
  cp "$ARTIFACTS"/../DesperateDevs/Editor/*.dll "$PACKAGE/Editor/DesperateDevs/"
fi
cp "$ARTIFACTS"/Analyzers/*.dll "$PACKAGE/Analyzers/"
# entitas-gen / entitas-rename, run by the editor. Unity ignores folders ending in "~", so
# they get no .meta and are never imported.
cp -R "$ARTIFACTS/Tools~" "$PACKAGE/Tools~"
cp "$SCRIPT_DIR/../../README.md" "$PACKAGE/README.md"
# Unity's Package Manager shows CHANGELOG.md as a tab on the package page.
cp "$SCRIPT_DIR/../../CHANGELOG.md" "$PACKAGE/CHANGELOG.md"
cp "$SCRIPT_DIR/LICENSE.txt" "$PACKAGE/LICENSE.md"

cat > "$PACKAGE/package.json" <<EOF
{
  "name": "com.bogenbai.entitas-flux",
  "version": "$VERSION",
  "displayName": "Entitas Flux",
  "description": "A fork of the Entitas ECS framework: atomic components, [Watched] deferred reactivity, safe component removal, a searchable component dropdown, and compile-time code generation with a Roslyn source generator.",
  "unity": "2022.3",
  "author": {
    "name": "Bogdan Kurilo",
    "url": "https://github.com/Bogenbai"
  },
  "documentationUrl": "https://github.com/Bogenbai/Entitas-Flux",
  "changelogUrl": "https://github.com/Bogenbai/Entitas-Flux/blob/master/CHANGELOG.md",
  "licensesUrl": "https://github.com/Bogenbai/Entitas-Flux/blob/master/LICENSE",
  "keywords": [ "entitas", "ecs", "entity", "component", "system", "source-generator" ]
}
EOF

# Every asset needs a .meta, with a guid derived from its path so it stays the same from
# one release to the next (see unity_meta.py).
python3 "$SCRIPT_DIR/unity_meta.py" package "$PACKAGE"
echo "package assembled at $PACKAGE"

echo
echo "Contents:"
find "$PACKAGE" -type f | sed "s|$PACKAGE/||" | sort
