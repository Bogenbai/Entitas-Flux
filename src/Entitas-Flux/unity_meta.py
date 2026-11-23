#!/usr/bin/env python3
"""Unity .meta files for what a release ships, shared by build.sh and package.sh.

  unity_meta.py analyzers <artifacts> <folder>   .meta for each analyzer DLL in <folder>
                                                 (relative to <artifacts>), for the archive
  unity_meta.py package <package>                .meta for every asset of the UPM package

A guid derived from the asset's path stays STABLE across releases: a changed guid makes
Unity treat the file as a brand new asset and breaks references to it.
"""
import hashlib
import os
import re
import sys


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


def analyzer(relative_path):
    return PLUGIN.format(guid=guid(relative_path), labels=ANALYZER_LABEL, any=0, editor=0)


def analyzers(artifacts, folder):
    """Without these a manual install got Unity's default import for the generator: a plugin
    enabled everywhere, never handed to the compiler, so nothing was generated."""
    directory = os.path.join(artifacts, folder)
    for name in sorted(os.listdir(directory)):
        if name.endswith(".dll"):
            write(os.path.join(directory, name), analyzer(folder + "/" + name))


def with_guid(meta_path, relative_path):
    """An asset whose importer settings come with it (the editor images, whose labels are how
    the editor finds them): its own .meta, with the package's guid."""
    with open(meta_path) as f:
        text = f.read()
    text, count = re.subn(r"(?m)^guid: [0-9a-f]+$", "guid: " + guid(relative_path), text)
    if count != 1:
        raise SystemExit("error: no guid line in " + meta_path)
    return text


def package(root):
    for directory, dirs, files in os.walk(root):
        # Folders ending in "~" (Tools~) are invisible to Unity: no .meta inside or for them.
        dirs[:] = sorted(d for d in dirs if not d.endswith("~"))
        for name in dirs:
            full = os.path.join(directory, name)
            write(full, FOLDER.format(guid=guid(os.path.relpath(full, root))))

        for name in sorted(files):
            if name.endswith(".meta"):
                continue

            full = os.path.join(directory, name)
            relative = os.path.relpath(full, root)
            folder = os.path.dirname(relative)

            if name.endswith(".dll"):
                is_analyzer = folder == "Analyzers"
                is_editor = folder == "Editor" or folder.startswith("Editor/")
                write(full, analyzer(relative) if is_analyzer else PLUGIN.format(
                    guid=guid(relative),
                    labels="",
                    any=0 if is_editor else 1,
                    editor=1 if is_editor else 0))
            elif name.endswith(".png"):
                if not os.path.exists(full + ".meta"):
                    raise SystemExit("error: " + relative + " came without its .meta")
                write(full, with_guid(full + ".meta", relative))
            else:
                write(full, TEXT.format(guid=guid(relative)))


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "analyzers":
        analyzers(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 3 and sys.argv[1] == "package":
        package(sys.argv[2])
    else:
        raise SystemExit(__doc__)
