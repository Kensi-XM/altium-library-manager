# Builds the Windows release: a self-contained program (no .NET to install) and, with Inno Setup 6, its installer.
#   .\build-release.ps1 -Version 1.0.1
param([string]$Version = '1.0.1')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'publish\win-x64'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'Altium Library Manager\Altium Library Manager.csproj') `
    -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

New-Item -ItemType Directory (Join-Path $root 'installer\output') -Force | Out-Null
$zip = Join-Path $root "installer\output\AltiumLibraryManager-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip

$iscc = @((Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $root 'installer\AltiumLibraryManager.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
}
else {
    Write-Warning 'Inno Setup 6 not found: only the zip was made.'
}
Get-ChildItem (Join-Path $root 'installer\output') | ForEach-Object { "$($_.Name)  $([math]::Round($_.Length / 1MB, 1)) MB" }
