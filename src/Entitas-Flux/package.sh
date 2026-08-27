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
cp "$ARTIFACTS"/Editor/*.dll "$PACKAGE/Editor/"
cp "$ARTIFACTS"/Analyzers/*.dll "$PACKAGE/Analyzers/"
cp "$SCRIPT_DIR/../../README.md" "$PACKAGE/README.md"
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
  "changelogUrl": "https://github.com/Bogenbai/Entitas-Flux/releases",
  "licensesUrl": "https://github.com/Bogenbai/Entitas-Flux/blob/master/LICENSE",
  "keywords": [ "entitas", "ecs", "entity", "component", "system", "source-generator" ]
}
EOF

# Every asset needs a .meta, and its guid must stay STABLE across releases — a changed
# guid makes Unity treat the file as a brand new asset and breaks references to it.
# Deriving the guid from the asset's path in the package keeps it reproducible.
python3 - "$PACKAGE" <<'PY'
import hashlib, os, sys

package = sys.argv[1]

def guid(relative_path):
    return hashlib.md5(("com.bogenbai.entitas-flux/" + relative_path).encode()).hexdigest()

FOLDER = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

TEXT = """fileFormatVersion: 2
guid: {guid}
TextScriptImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

# Managed plugin. `any`/`editor` decide which platforms the DLL is enabled for;
# analyzers are enabled nowhere and carry the RoslynAnalyzer label instead, which is
# what makes Unity hand them to the compiler rather than load them as plugins.
PLUGIN = """fileFormatVersion: 2
guid: {guid}
{labels}PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: {any}
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: {editor}
      settings:
        DefaultValueInitialized: true
  - first:
      Windows Store Apps: WindowsStoreApps
    second:
      enabled: 0
      settings:
        CPU: AnyCPU
  userData:
  assetBundleName:
  assetBundleVariant:
"""

ANALYZER_LABEL = "labels:\n- RoslynAnalyzer\n"

def write(path, content):
    with open(path + ".meta", "w") as f:
        f.write(content)

for root, dirs, files in os.walk(package):
    dirs.sort()
    for directory in dirs:
        full = os.path.join(root, directory)
        relative = os.path.relpath(full, package)
        write(full, FOLDER.format(guid=guid(relative)))

    for name in sorted(files):
        if name.endswith(".meta"):
            continue

        full = os.path.join(root, name)
        relative = os.path.relpath(full, package)
        folder = os.path.dirname(relative)

        if name.endswith(".dll"):
            is_analyzer = folder == "Analyzers"
            is_editor = folder == "Editor"
            write(full, PLUGIN.format(
                guid=guid(relative),
                labels=ANALYZER_LABEL if is_analyzer else "",
                any=0 if (is_analyzer or is_editor) else 1,
                editor=1 if is_editor else 0))
        else:
            write(full, TEXT.format(guid=guid(relative)))

print("package assembled at", package)
PY

echo
echo "Contents:"
find "$PACKAGE" -type f | sed "s|$PACKAGE/||" | sort
