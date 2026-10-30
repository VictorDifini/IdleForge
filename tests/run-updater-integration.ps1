param([string]$DotNet = ".\work\.dotnet\dotnet.exe")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("IdleForge-updater-it-" + [guid]::NewGuid().ToString("N"))
$install = Join-Path $sandbox "install"
$staging = Join-Path $sandbox "staging"
$backup = Join-Path $sandbox "backup"
$updater = Join-Path $sandbox "updater"
$health = Join-Path $sandbox "health.ok"
$plan = Join-Path $sandbox "plan.json"
$token = [guid]::NewGuid().ToString("N")

function New-Manifest([string]$Directory, [string]$Version) {
    $files = Get-ChildItem -LiteralPath $Directory -File -Recurse | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; Length = $_.Length }
    }
    [ordered]@{ Product = "IdleForge"; Version = $Version; CreatedAt = [DateTimeOffset]::UtcNow; Files = @($files) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory "idleforge-update.json") -Encoding utf8
}

New-Item -ItemType Directory -Force -Path $install, $staging, $backup, $updater | Out-Null
try {
    & $DotNet publish (Join-Path $root "tests/IdleForge.UpdateProbe/IdleForge.UpdateProbe.csproj") -c Release -r win-x64 --self-contained false -o $install /p:Version=1.0.0 | Out-Null
    & $DotNet publish (Join-Path $root "tests/IdleForge.UpdateProbe/IdleForge.UpdateProbe.csproj") -c Release -r win-x64 --self-contained false -o $staging /p:Version=2.0.0 | Out-Null
    & $DotNet publish (Join-Path $root "src/IdleForge.Updater/IdleForge.Updater.csproj") -c Release -r win-x64 --self-contained true -o $updater /p:PublishSingleFile=true /p:DebugType=None | Out-Null
    New-Manifest $staging "2.0.0"
    [ordered]@{ InstallDirectory = $install; StagingDirectory = $staging; BackupDirectory = $backup; MainExecutable = "IdleForge.App.exe"; ParentProcessId = 2147483646; HealthFile = $health; Token = $token; HealthTimeoutSeconds = 5 } | ConvertTo-Json | Set-Content -LiteralPath $plan -Encoding utf8

    $process = Start-Process -FilePath (Join-Path $updater "IdleForge.Updater.exe") -ArgumentList @("--plan", ('"' + $plan + '"')) -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Atualizador retornou $($process.ExitCode)." }
    if ((Get-Content -LiteralPath $health -Raw) -ne $token) { throw "A nova versão não confirmou o token." }
    if (-not (Test-Path -LiteralPath (Join-Path $backup "IdleForge.App.exe"))) { throw "Backup da versão anterior ausente." }
    if ((Get-Content -LiteralPath (Join-Path $install "probe-started.txt") -Raw) -notmatch '^2\.0\.0') { throw "A versão nova não reiniciou." }

    $failedStaging = Join-Path $sandbox "failed-staging"
    $rollbackBackup = Join-Path $sandbox "rollback-backup"
    $rollbackHealth = Join-Path $sandbox "rollback-health.ok"
    $rollbackPlan = Join-Path $sandbox "rollback-plan.json"
    New-Item -ItemType Directory -Force -Path $failedStaging, $rollbackBackup | Out-Null
    Copy-Item -LiteralPath "$env:WINDIR\System32\where.exe" -Destination (Join-Path $failedStaging "IdleForge.App.exe")
    New-Manifest $failedStaging "3.0.0"
    $installedHash = (Get-FileHash -LiteralPath (Join-Path $install "IdleForge.App.exe") -Algorithm SHA256).Hash
    [ordered]@{ InstallDirectory = $install; StagingDirectory = $failedStaging; BackupDirectory = $rollbackBackup; MainExecutable = "IdleForge.App.exe"; ParentProcessId = 2147483646; HealthFile = $rollbackHealth; Token = "rollback-test"; HealthTimeoutSeconds = 2 } | ConvertTo-Json | Set-Content -LiteralPath $rollbackPlan -Encoding utf8
    $rollbackProcess = Start-Process -FilePath (Join-Path $updater "IdleForge.Updater.exe") -ArgumentList @("--plan", ('"' + $rollbackPlan + '"')) -PassThru -Wait
    Start-Sleep -Milliseconds 500
    if ($rollbackProcess.ExitCode -ne 1) { throw "A atualização defeituosa não acionou rollback." }
    if ((Get-FileHash -LiteralPath (Join-Path $install "IdleForge.App.exe") -Algorithm SHA256).Hash -ne $installedHash) { throw "O executável anterior não foi restaurado." }
    Write-Output "PASS: substituição, backup, reinício, health check e rollback reais em ambiente temporário."
}
finally {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
}
