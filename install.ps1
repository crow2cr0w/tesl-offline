param([string]$GameDir)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/tools/manage-install.ps1" -Action Install -Root $PSScriptRoot -GameDir $GameDir
