# Gera installer\Orbit2Setup.exe: publica self-contained (pasta, não single-file) e compila o .iss.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

dotnet publish "$root\Orbit\Orbit.csproj" -c Release -r win-x64 --self-contained
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# O SDK copia o loader nativo (raiz ou runtimes); o instalador e o zip preservam a pasta inteira.
$publish = Join-Path $root 'Orbit\bin\Release\net10.0-windows10.0.22621.0\win-x64\publish'
$loader = (Test-Path (Join-Path $publish 'WebView2Loader.dll')) -or
    (Test-Path (Join-Path $publish 'runtimes\win-x64\native\WebView2Loader.dll'))
if (-not $loader -or
    -not (Test-Path (Join-Path $publish 'Microsoft.Web.WebView2.Core.dll'))) {
    throw 'Publish sem WebView2Loader.dll/Core.dll. Não gere um instalador incompleto.'
}

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado (winget install JRSoftware.InnoSetup).' }

& $iscc "$PSScriptRoot\Orbit.iss"
exit $LASTEXITCODE
