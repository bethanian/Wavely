$ErrorActionPreference = 'Stop'
$Installer = Join-Path $env:TEMP 'wavely-dotnet-install.ps1'
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $Installer
& $Installer -Version '10.0.403' -InstallDir (Join-Path $env:LOCALAPPDATA 'Wavely\dotnet') -NoPath
if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed.' }
