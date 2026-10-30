param([int]$Profiles = 10)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $repo 'work/.dotnet/dotnet.exe'
$log = Join-Path $PSScriptRoot ("soak-8h-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.jsonl')
& $dotnet run --project (Join-Path $PSScriptRoot 'IdleForge.WebView2.Audit/IdleForge.WebView2.Audit.csproj') -c Release -- $Profiles '--soak-seconds=28800' 2>&1 | Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { throw "Soak test falhou. Consulte $log" }
