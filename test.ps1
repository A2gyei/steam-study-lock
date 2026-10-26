$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('SteamStudyLock-Test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$testExe = Join-Path $testDir 'SteamStudyLock-test.exe'
$resultFile = Join-Path $testDir 'self-test.txt'
try {
    # No administrator manifest: these tests never change system settings.
    & $compiler /nologo /target:exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$testExe" (Join-Path $PSScriptRoot 'SteamStudyLock.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testExe --self-test
    if ($LASTEXITCODE -ne 0) { throw "Self-test failed: $LASTEXITCODE" }
    Get-Content -LiteralPath $resultFile
    $fixtureExe = Join-Path $testDir 'GameFixture.exe'
    $closureExe = Join-Path $testDir 'GameClosureTests.exe'
    & $compiler /nologo /target:winexe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$fixtureExe" (Join-Path $PSScriptRoot 'GameFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    & $compiler /nologo /target:exe /main:GameClosureTests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$closureExe" (Join-Path $PSScriptRoot 'SteamStudyLock.cs') (Join-Path $PSScriptRoot 'GameClosureTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Game closure test compilation failed.' }
    & $closureExe
    if ($LASTEXITCODE -ne 0) { throw 'Game closure tests failed.' }
} finally {
    if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe }
    if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile }
    foreach ($name in @('GameFixture.exe', 'GameClosureTests.exe')) {
        $testFile = Join-Path $testDir $name
        if (Test-Path -LiteralPath $testFile) { Remove-Item -LiteralPath $testFile }
    }
    Remove-Item -LiteralPath $testDir
}
