# Builds WiFile.exe, runs the end-to-end tests and produces dist\WiFile-Setup-<version>.exe
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = '1.2.1'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk\dotnet.exe'
if (Test-Path $userSdk) { $dotnet = $userSdk }
if (-not $dotnet) { throw 'The .NET SDK (8 or newer) is required to build.' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required (winget install JRSoftware.InnoSetup).' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

Write-Host '== Building WiFile' -ForegroundColor Cyan
& $dotnet build "$root\src\WiFile\WiFile.csproj" -c Release -p:Version=$version -nologo -v q
if ($LASTEXITCODE) { throw 'build failed' }

if (-not $SkipTests) {
    Write-Host '== Running end-to-end tests' -ForegroundColor Cyan
    & $dotnet build "$root\tests\WiFile.Tests\WiFile.Tests.csproj" -c Release -nologo -v q
    if ($LASTEXITCODE) { throw 'test build failed' }
    & "$root\tests\WiFile.Tests\bin\Release\net48\WiFile.Tests.exe"
    if ($LASTEXITCODE) { throw 'tests failed' }
}

Write-Host '== Staging' -ForegroundColor Cyan
$build = "$root\out\stage"
Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $build -Force | Out-Null
Copy-Item "$root\src\WiFile\bin\Release\net48\WiFile.exe" $build
Copy-Item "$root\src\WiFile\bin\Release\net48\WiFile.exe.config" $build -ErrorAction SilentlyContinue

$sign = [bool]($env:WIFILE_CERT_THUMBPRINT -or $env:WIFILE_CERT_PFX)
if ($sign) {
    Write-Host '== Signing WiFile.exe' -ForegroundColor Cyan
    & "$root\sign.ps1" "$build\WiFile.exe"
} else {
    Write-Warning 'No code-signing certificate configured (see sign.ps1): the installer will be unsigned and SmartScreen will warn on download.'
}

Write-Host '== Packaging installer' -ForegroundColor Cyan
$isccArgs = @('/Q', "/DAppVersion=$version")
if ($sign) {
    # Inno Setup calls this for the setup exe and the uninstaller; $f is the file to sign.
    $isccArgs += "/Swifisign=powershell.exe -NoProfile -ExecutionPolicy Bypass -File `$q$root\sign.ps1`$q `$q`$f`$q"
    $isccArgs += '/DSign'
}
& $iscc @isccArgs "$root\installer\WiFile.iss"
if ($LASTEXITCODE) { throw 'installer build failed' }
Get-Item "$root\dist\WiFile-Setup-$version.exe" | Select-Object FullName, @{n='SizeKB';e={[int]($_.Length/1KB)}}
