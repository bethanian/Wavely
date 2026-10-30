param([switch]$Test, [switch]$Devices, [switch]$Package)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $Root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$LocalSdk = Join-Path $env:LOCALAPPDATA 'Wavely\dotnet\dotnet.exe'
$Dotnet = if (Test-Path -LiteralPath $LocalSdk) { $LocalSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
function Invoke-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Invoke-Dotnet @('build', 'src/Wavely.App', '-c', 'Release', '-p:Platform=x64', '-p:RestoreLockedMode=true')
if ($Test -or $Devices) {
    $Arguments = @('run', '--project', 'tests/Wavely.Tests', '-c', 'Release', '--')
    if ($Devices) { $Arguments += '--devices' }
    Invoke-Dotnet $Arguments
}
if ($Package) {
    $Output = Join-Path $Root 'artifacts/Wavely-1.0.1-win-x64'
    Invoke-Dotnet @('publish', 'src/Wavely.App', '-c', 'Release', '-p:Platform=x64', '-o', $Output, '--self-contained', 'true')
    foreach ($Required in @('Wavely.exe', 'Wavely.pri', 'App.xbf', 'MainWindow.xbf', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'Assets/Wavely.ico')) {
        if (!(Test-Path -LiteralPath (Join-Path $Output $Required))) { throw "Release is missing $Required" }
    }
    Copy-Item -LiteralPath (Join-Path $Root 'LICENSE'), (Join-Path $Root 'README.md'), (Join-Path $Root 'THIRD-PARTY-NOTICES.md') -Destination $Output
    Copy-Item -LiteralPath (Join-Path $Root 'docs') -Destination $Output -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $Root 'licenses') -Destination $Output -Recurse -Force
    $Archive = "$Output.zip"
    Compress-Archive -Path "$Output\*" -DestinationPath $Archive -Force
    Get-FileHash -LiteralPath $Archive -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($Archive))" } | Set-Content -LiteralPath "$Archive.sha256" -Encoding ascii
    Write-Output "Release: $Archive"
}
