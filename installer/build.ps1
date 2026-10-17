# Gera installer\Orbit2Setup.exe: publica self-contained (pasta, não single-file) e compila o .iss.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

dotnet publish "$root\Orbit\Orbit.csproj" -c Release -r win-x64 --self-contained
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado (winget install JRSoftware.InnoSetup).' }

& $iscc "$PSScriptRoot\Orbit.iss"
exit $LASTEXITCODE
