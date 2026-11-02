param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Uninstall')][string]$Action,
    [Parameter(Mandatory = $true)][string]$Root,
    [string]$GameDir
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$knownRetail = 'ab4b71b62a2dcf268fafc8decc26bd0eaa2e5c09679e566ad7f24f31ffc5fb0a'
$legacy = @('dc5c522a428219162a57ecb97cbf1bb51973376a8a30a1f6f472b22fe01a37cf',
            '7b2e6ca90e7c48948159d64397ae19cdaae7769031dffa8cf5b6929018fc76b7')
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-Entries([string]$Path) {
    $entries = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $parts = $line -split '=', 2
        if ($parts.Length -eq 2) {
            if ($entries.ContainsKey($parts[0])) { throw "Duplicate field in ${Path}: $($parts[0])" }
            $entries[$parts[0]] = $parts[1]
        }
    }
    return $entries
}
function Required($Entries, [string]$Key) {
    if (-not $Entries.ContainsKey($Key)) { throw "Missing field: $Key" }
    return $Entries[$Key]
}
function Validate-Hash([string]$Value) {
    if ($Value -cnotmatch '^[0-9a-f]{64}$') { throw "Invalid SHA-256: $Value" }
}
function Replace-Verified([string]$Source, [string]$Target, [string]$Expected) {
    $temp = Join-Path ([IO.Path]::GetDirectoryName($Target)) ([IO.Path]::GetRandomFileName())
    try {
        [IO.File]::Copy($Source, $temp)
        if ((Hash $temp) -ne $Expected) { throw 'Temporary copy failed verification' }
        [IO.File]::Replace($temp, $Target, [NullString]::Value)
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
    }
}
if (-not $GameDir) { $GameDir = $env:GAME_DIR }
if (-not $GameDir) {
    $GameDir = Join-Path (Split-Path -Parent $Root) 'The Elder Scrolls Legends'
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath
$managed = Join-Path $GameDir 'The Elder Scrolls Legends_Data/Managed'
$target = Join-Path $managed 'Assembly-CSharp.dll'
$statePath = Join-Path $managed 'NyxTESLOffline.install-state'
$data = Join-Path $managed 'NyxTESLOffline-data'
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Game DLL not found: $target" }
$current = Hash $target
$state = $null
if (Test-Path -LiteralPath $statePath) {
    $state = Read-Entries $statePath
    if ((Required $state 'state_version') -ne '1') { throw 'Incompatible installation manifest' }
    if ($current -ne (Required $state 'installed_sha')) { throw 'Installed DLL changed since the last recorded installation' }
}
$seeds = @(
    @{ Name = 'inventory-seed.json'; Flag = 'created_inventory_seed'; Sha = 'inventory_seed_sha' },
    @{ Name = 'campaigns-seed.json'; Flag = 'created_campaigns_seed'; Sha = 'campaigns_seed_sha' }
)
if ($Action -eq 'Install') {
    $buildDir = $Root
    if (Test-Path -LiteralPath (Join-Path $Root 'src') -PathType Container) { $buildDir = Join-Path $Root 'build' }
    $built = Join-Path $buildDir 'Assembly-CSharp.dll'
    $manifest = Read-Entries (Join-Path $buildDir 'manifest.txt')
    $baseSha = Required $manifest 'retail_sha256'
    $modSha = Required $manifest 'patched_sha256'
    Validate-Hash $baseSha
    Validate-Hash $modSha
    if ((Hash $built) -ne $modSha) { throw 'Mod DLL does not match its manifest' }
    $backupName = "Assembly-CSharp.dll.nyx-original-$baseSha"
    $backup = Join-Path $managed $backupName
    if ($state) {
        if ((Required $state 'backup_file') -ne $backupName) { throw 'Build and installation use different retail versions' }
    } elseif ($current -ne $baseSha -and $current -ne $modSha -and $current -notin $legacy) {
        throw "Unknown current DLL; installation refused (hash $current)"
    }
    # Verify all seed files before changing the DLL.
    foreach ($seed in $seeds) { $seed.SourceSha = Hash (Join-Path $Root "data/$($seed.Name)") }
    if (Test-Path -LiteralPath $backup) {
        if ((Hash $backup) -ne $baseSha) { throw 'Original backup failed verification' }
    } elseif ($current -eq $baseSha -and -not $state) {
        [IO.File]::Copy($target, $backup)
        if ((Hash $backup) -ne $baseSha) { throw 'Original backup failed verification' }
    } else { throw 'Verified original backup is missing' }
    $createdDir = '0'
    if ($state) { $createdDir = Required $state 'created_data_dir' }
    if (-not (Test-Path -LiteralPath $data)) { [IO.Directory]::CreateDirectory($data) | Out-Null; $createdDir = '1' }
    $newState = @('state_version=1', "installed_sha=$modSha", "backup_file=$backupName", "retail_sha256=$baseSha", "created_data_dir=$createdDir")
    foreach ($seed in $seeds) {
        $created = '0'
        if ($state) { $created = Required $state $seed.Flag }
        $destination = Join-Path $data $seed.Name
        if (-not (Test-Path -LiteralPath $destination)) {
            [IO.File]::Copy((Join-Path $Root "data/$($seed.Name)"), $destination)
            $created = '1'
        }
        $newState += "$($seed.Flag)=$created", "$($seed.Sha)=$($seed.SourceSha)"
    }
    $graphSource = Join-Path $Root 'data/retail-rule-graphs.jsonl.gz'
    $graphDestination = Join-Path $data 'retail-rule-graphs.jsonl.gz'
    $graphSha = Hash $graphSource
    $createdGraph = '0'
    if ($state -and $state.ContainsKey('created_graph')) { $createdGraph = $state['created_graph'] }
    if (Test-Path -LiteralPath $graphDestination) {
        $previousGraphSha = $graphSha
        if ($state -and $state.ContainsKey('graph_sha')) { $previousGraphSha = $state['graph_sha'] }
        if ((Hash $graphDestination) -ne $previousGraphSha) { throw 'Installed retail graph was modified; copying refused' }
    } else { $createdGraph = '1' }
    [IO.File]::Copy($graphSource, $graphDestination, $true)
    $newState += "created_graph=$createdGraph", "graph_sha=$graphSha"
    $fxSource = Join-Path $Root 'data/retail-rule-fx.tsv'
    $fxDestination = Join-Path $data 'retail-rule-fx.tsv'
    $fxSha = Hash $fxSource
    $createdFx = '0'
    if ($state -and $state.ContainsKey('created_fx')) { $createdFx = $state['created_fx'] }
    if (Test-Path -LiteralPath $fxDestination) {
        $previousFxSha = $fxSha
        if ($state -and $state.ContainsKey('fx_sha')) { $previousFxSha = $state['fx_sha'] }
        if ((Hash $fxDestination) -ne $previousFxSha) { throw 'Installed retail FX mapping was modified; copying refused' }
    } else { $createdFx = '1' }
    [IO.File]::Copy($fxSource, $fxDestination, $true)
    $newState += "created_fx=$createdFx", "fx_sha=$fxSha"
    if ($current -ne $modSha) { Replace-Verified $built $target $modSha }
    $stateTemp = Join-Path $managed ([IO.Path]::GetRandomFileName())
    try {
        [IO.File]::WriteAllLines($stateTemp, [string[]]$newState, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $statePath) { [IO.File]::Replace($stateTemp, $statePath, [NullString]::Value) }
        else { [IO.File]::Move($stateTemp, $statePath) }
    } finally { if (Test-Path -LiteralPath $stateTemp) { Remove-Item -LiteralPath $stateTemp } }
    if ((Hash $target) -ne $modSha) { throw 'Final DLL verification failed' }
    Write-Output "Mod installed in: $GameDir"
    Write-Output "SHA-256: $modSha"
    Write-Output "Original DLL preserved at: $backup"
} else {
    $baseSha = $knownRetail
    if ($state -and $state.ContainsKey('retail_sha256')) { $baseSha = $state['retail_sha256'] }
    Validate-Hash $baseSha
    $backupName = "Assembly-CSharp.dll.nyx-original-$baseSha"
    if ($state) {
        if ((Required $state 'backup_file') -ne $backupName) { throw 'Unexpected backup in manifest' }
        foreach ($seed in $seeds) { Required $state $seed.Flag | Out-Null; Required $state $seed.Sha | Out-Null }
        Required $state 'created_data_dir' | Out-Null
    } elseif ($current -notin $legacy) { throw 'Manifest missing; restoration refused' }
    $backup = Join-Path $managed $backupName
    if ((Hash $backup) -ne $baseSha) { throw 'Original backup failed verification' }
    Replace-Verified $backup $target $baseSha
    if ($state) {
        if ($state.ContainsKey('created_test_decks_seed')) {
            $seeds += @{ Name = 'recovered-test-decks.state'; Flag = 'created_test_decks_seed'; Sha = 'recovered_test_decks_seed_sha' }
        }
        foreach ($seed in $seeds) {
            $seedPath = Join-Path $data $seed.Name
            if ($state[$seed.Flag] -eq '1' -and (Test-Path -LiteralPath $seedPath)) {
                if ((Hash $seedPath) -eq $state[$seed.Sha]) { Remove-Item -LiteralPath $seedPath }
                else { Write-Output "Preserving modified file: $($seed.Name)" }
            }
        }
        if ($state.ContainsKey('created_graph') -and $state['created_graph'] -eq '1') {
            $graphDestination = Join-Path $data 'retail-rule-graphs.jsonl.gz'
            if (Test-Path -LiteralPath $graphDestination) {
                if ((Hash $graphDestination) -eq $state['graph_sha']) { Remove-Item -LiteralPath $graphDestination }
                else { Write-Output 'Preserving retail graph modified after installation.' }
            }
        }
        if ($state.ContainsKey('created_fx') -and $state['created_fx'] -eq '1') {
            $fxDestination = Join-Path $data 'retail-rule-fx.tsv'
            if (Test-Path -LiteralPath $fxDestination) {
                if ((Hash $fxDestination) -eq $state['fx_sha']) { Remove-Item -LiteralPath $fxDestination }
                else { Write-Output 'Preserving FX mapping modified after installation.' }
            }
        }
        Remove-Item -LiteralPath $statePath
        if ($state['created_data_dir'] -eq '1' -and (Test-Path -LiteralPath $data) -and @(Get-ChildItem -LiteralPath $data -Force).Count -eq 0) {
            [IO.Directory]::Delete($data)
        }
    }
    if ((Hash $target) -ne $baseSha) { throw 'DLL restoration failed' }
    Write-Output "Mod removed; original DLL restored at: $target"
}
