#!/usr/bin/env pwsh
param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# script dir, solution, and folders
$ScriptDir   = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -LiteralPath $MyInvocation.MyCommand.Path -Parent }
$Solution    = Join-Path $ScriptDir "Entitas.sln"
$TmpOut      = Join-Path $ScriptDir ".buildout"
$Artifacts   = Join-Path $ScriptDir "Artifacts"

# clean outputs
if (Test-Path $TmpOut) { Remove-Item $TmpOut -Recurse -Force }
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

# copy only primary project DLLs (exclude tests and unity editor tooling)
$csprojs = Get-ChildItem -Path $ScriptDir -Recurse -Filter *.csproj |
  Where-Object { $_.Name -notmatch '(?i)test|tests' }

foreach ($proj in $csprojs) {
  $nameFromFile = [IO.Path]::GetFileNameWithoutExtension($proj.Name)
  $dllCandidate = Join-Path $TmpOut "$nameFromFile.dll"

  if (Test-Path $dllCandidate) {
    Copy-Item $dllCandidate -Destination $Artifacts -Force
    continue
  }

  # try AssemblyName if it differs from project file name
  $asmName = (& dotnet msbuild $proj.FullName -nologo -getProperty:AssemblyName 2>$null | Select-Object -Last 1).Trim()
  if ($asmName) {
    $dllCandidate2 = Join-Path $TmpOut "$asmName.dll"
    if (Test-Path $dllCandidate2) {
      Copy-Item $dllCandidate2 -Destination $Artifacts -Force
    }
  }
}

# optional: remove temp build folder
Remove-Item $TmpOut -Recurse -Force

Write-Host "Artifacts:"
Get-ChildItem $Artifacts | Select-Object -ExpandProperty Name
Write-Host "Done → $Artifacts"
