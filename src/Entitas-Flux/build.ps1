# if Windows refuse to run the script, try next command in powershell: powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

#!/usr/bin/env pwsh
param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# script dir, solution, and folders
$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -LiteralPath $MyInvocation.MyCommand.Path -Parent }
$Solution  = Join-Path $ScriptDir "Entitas.sln"
$TmpOut    = Join-Path $ScriptDir ".buildout"
$Artifacts = Join-Path $ScriptDir "Artifacts"

# clean outputs
if (Test-Path $TmpOut)    { Remove-Item $TmpOut -Recurse -Force }
if (Test-Path $Artifacts) { Remove-Item $Artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $TmpOut, $Artifacts | Out-Null

# common msbuild props (note the trailing separator in OutDir)
$sep = [IO.Path]::DirectorySeparatorChar
$MSBuildProps = @(
  "-p:OutDir=$TmpOut$sep"
  "-p:AppendTargetFrameworkToOutputPath=false"
  "-p:AppendRuntimeIdentifierToOutputPath=false"
  "-p:DebugSymbols=false"
  "-p:DebugType=None"
)

# build
& dotnet clean $Solution -c $Configuration | Out-Null
& dotnet build $Solution -c $Configuration @MSBuildProps

# mapping: dll -> relative destination under Artifacts/
$Map = @{
  "Entitas.CodeGeneration.Plugins.dll"          = "Jenny/Jenny/Plugins/Entitas"
  "Entitas.Roslyn.CodeGeneration.Plugins.dll"   = "Jenny/Jenny/Plugins/Entitas"
  "Entitas.VisualDebugging.CodeGeneration.Plugins.dll" = "Jenny/Jenny/Plugins/Entitas"

  "Entitas.CodeGeneration.Attributes.dll"       = "Assets/Entitas/Entitas"
  "Entitas.dll"                                 = "Assets/Entitas/Entitas"
  "Entitas.Unity.dll"                           = "Assets/Entitas/Entitas"
  "Entitas.VisualDebugging.Unity.dll"           = "Assets/Entitas/Entitas"

  "Entitas.Migration.dll"                       = "Assets/Entitas/Entitas/Editor"
  "Entitas.Migration.Unity.Editor.dll"          = "Assets/Entitas/Entitas/Editor"
  "Entitas.Unity.Editor.dll"                    = "Assets/Entitas/Entitas/Editor"
  "Entitas.VisualDebugging.Unity.Editor.dll"    = "Assets/Entitas/Entitas/Editor"
}

# copy only mapped DLLs from .buildout → Artifacts/<dest>
$missing = @()
foreach ($kv in $Map.GetEnumerator()) {
  $dll = $kv.Key
  $destRel = $kv.Value
  $src = Join-Path $TmpOut $dll

  if (Test-Path $src) {
    $destDir = Join-Path $Artifacts $destRel
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    Copy-Item $src -Destination $destDir -Force
  } else {
    $missing += $dll
  }
}

# optional: remove temp build folder
Remove-Item $TmpOut -Recurse -Force

if ($missing.Count -gt 0) {
  Write-Warning ("Expected DLL(s) not found in build output: " + ($missing -join ", "))
}

Write-Host "Resulting layout (no DLLs in Artifacts/ root):"
Get-ChildItem $Artifacts -Recurse -File -Filter *.dll |
  ForEach-Object {
    $_.FullName.Replace("$Artifacts$([IO.Path]::DirectorySeparatorChar)", "")
  }

Write-Host "Done → $Artifacts"
