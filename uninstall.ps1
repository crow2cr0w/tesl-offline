param([string]$GameDir)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/tools/manage-install.ps1" -Action Uninstall -Root $PSScriptRoot -GameDir $GameDir
