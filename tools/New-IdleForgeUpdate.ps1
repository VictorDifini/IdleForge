param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$DotNet = "dotnet"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
$staging = Join-Path $outputRootFull (".building-" + $Version + "-" + [guid]::NewGuid().ToString("N"))
$final = Join-Path $outputRootFull ("IdleForge-" + $Version + "-win-x64")
$updaterPublish = Join-Path ([IO.Path]::GetTempPath()) ("IdleForge-updater-" + [guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Force -Path $outputRootFull, $staging, $updaterPublish | Out-Null
try {
    & $DotNet publish (Join-Path $projectRoot "src/IdleForge.App/IdleForge.App.csproj") -c Release -r win-x64 --self-contained true -o $staging /p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar o IdleForge." }

    & $DotNet publish (Join-Path $projectRoot "src/IdleForge.Updater/IdleForge.Updater.csproj") -c Release -r win-x64 --self-contained true -o $updaterPublish /p:PublishSingleFile=true /p:DebugType=None /p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar o atualizador." }
    Copy-Item -LiteralPath (Join-Path $updaterPublish "IdleForge.Updater.exe") -Destination $staging

    $files = Get-ChildItem -LiteralPath $staging -File -Recurse | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($staging, $_.FullName).Replace('\', '/')
        [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; length = $_.Length }
    }
    $manifest = [ordered]@{ product = "IdleForge"; version = $Version; createdAt = [DateTimeOffset]::UtcNow; files = @($files) }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging "idleforge-update.json") -Encoding utf8

    if (Test-Path -LiteralPath $final) { throw "O pacote final já existe: $final" }
    Move-Item -LiteralPath $staging -Destination $final
    Write-Output $final
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    if (Test-Path -LiteralPath $updaterPublish) { Remove-Item -LiteralPath $updaterPublish -Recurse -Force }
}
