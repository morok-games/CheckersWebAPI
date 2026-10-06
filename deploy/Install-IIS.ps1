#Requires -RunAsAdministrator
# Deploys the published API (dotnet publish Checkers.Api -c Release -o <PublishPath>) to IIS.
# Engine and database paths are taken from <PublishPath>\appsettings.json (Engine:Path, Engine:Databases).
# Needs IIS and the ASP.NET Core 10 Hosting Bundle. Example:  .\Install-IIS.ps1 -PublishPath C:\inetpub\checkers
param(
    [Parameter(Mandatory)] [string] $PublishPath,
    [string] $Name = "CheckersApi",
    [int] $Port = 8080
)
$ErrorActionPreference = "Stop"
$engine = (Get-Content (Join-Path $PublishPath "appsettings.json") -Raw | ConvertFrom-Json).Engine
if (-not (Test-Path $engine.Path)) { throw "Engine:Path from appsettings.json not found: $($engine.Path)" }
Import-Module WebAdministration
if (-not (Get-WebGlobalModule -Name AspNetCoreModuleV2 -ErrorAction SilentlyContinue)) { throw "Install the ASP.NET Core 10 Hosting Bundle, then run iisreset." }

# Site preload (start the engine with IIS, not with the first request) needs Application Initialization.
if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) { Install-WindowsFeature Web-AppInit | Out-Null }
else { Enable-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -All -NoRestart | Out-Null }

# App pool for long-lived engine workers: no managed code, 64-bit, always running, never recycled on a timer,
# and no overlapped recycling (two sets of workers must not run at the same time).
$pool = "IIS:\AppPools\$Name"
if (-not (Test-Path $pool)) { New-WebAppPool -Name $Name | Out-Null }
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ""
Set-ItemProperty $pool -Name enable32BitAppOnWin64 -Value $false
Set-ItemProperty $pool -Name startMode -Value AlwaysRunning
Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name recycling.disallowOverlappingRotation -Value $true

$site = "IIS:\Sites\$Name"
if (-not (Test-Path $site)) { New-Website -Name $Name -Port $Port -PhysicalPath $PublishPath -ApplicationPool $Name | Out-Null }
Set-ItemProperty $site -Name physicalPath -Value $PublishPath
Set-ItemProperty $site -Name applicationDefaults.preloadEnabled -Value $true

# The app pool identity reads the site, the CheckerBoard folder (engine + egdb64.dll) and the databases,
# and writes .\logs (request log and the engine's own log).
$identity = "IIS AppPool\$Name"
New-Item -ItemType Directory -Force (Join-Path $PublishPath "logs") | Out-Null
icacls $PublishPath /grant "${identity}:(OI)(CI)RX" /T /Q | Out-Null
icacls (Join-Path $PublishPath "logs") /grant "${identity}:(OI)(CI)M" /T /Q | Out-Null
icacls (Split-Path (Split-Path $engine.Path)) /grant "${identity}:(OI)(CI)RX" /T /Q | Out-Null
if ($engine.Databases) { icacls $engine.Databases /grant "${identity}:(OI)(CI)RX" /T /Q | Out-Null }

Restart-WebAppPool $Name
Start-Website $Name
foreach ($i in 1..30) {
    Start-Sleep 2
    try { if ((Invoke-RestMethod "http://localhost:$Port/healthz").ok) { Write-Host "OK: http://localhost:$Port/"; exit 0 } } catch { }
}
throw "No healthy answer from http://localhost:$Port/healthz, see $PublishPath\logs\stdout_*.log"
