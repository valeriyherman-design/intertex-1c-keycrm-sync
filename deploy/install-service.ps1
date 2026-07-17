# Запускать в PowerShell ОТ ИМЕНИ АДМИНИСТРАТОРА, из папки с IntertexSync.Service.exe
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $dir "IntertexSync.Service.exe"
if (-not (Test-Path $exe)) { throw "Не найден $exe" }
New-Item -ItemType Directory -Force -Path (Join-Path $dir "data"),(Join-Path $dir "logs") | Out-Null
if (Get-Service IntertexSync -ErrorAction SilentlyContinue) { sc.exe stop IntertexSync | Out-Null; Start-Sleep 2; sc.exe delete IntertexSync | Out-Null; Start-Sleep 1 }
sc.exe create IntertexSync binPath= "`"$exe`"" start= auto DisplayName= "IntertexSync (KeyCRM 1C)"
sc.exe description IntertexSync "Интеграция KeyCRM и 1С:УТП (InterTex)"
Write-Host "Служба создана. Проверьте secrets.json, затем: sc.exe start IntertexSync"
Write-Host "Здоровье: откройте http://localhost:5199/health"
