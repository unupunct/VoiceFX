$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (Test-Path publish) { Remove-Item -LiteralPath "$root\publish" -Recurse -Force }
dotnet publish src\VoiceFX.App -c Release -r win-x64 --self-contained true -p:DebugType=none -o publish
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found' }
& $iscc installer\VoiceFX.iss
