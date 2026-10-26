$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /platform:anycpu /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /win32manifest:app.manifest /out:SteamStudyLock.exe SteamStudyLock.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Write-Host 'Built SteamStudyLock.exe'
} finally { Pop-Location }
