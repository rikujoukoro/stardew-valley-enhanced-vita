$ErrorActionPreference = 'Stop'
$toolDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $toolDir '../../../../')).Path
$outDir = Join-Path $repoRoot 'scratchpad/texture-prebake'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$source = Join-Path $toolDir 'TexturePrebake.cs'
$fna = Join-Path $repoRoot 'FNA/bin/Release/net4.0/FNA.dll'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Copy-Item -LiteralPath $fna -Destination (Join-Path $outDir 'FNA.dll') -Force
Push-Location $toolDir
try {
    & $compiler /nologo "/out:$(Join-Path $outDir 'TexturePrebake.exe')" "/reference:$(Join-Path $outDir 'FNA.dll')" 'TexturePrebake.cs'
    if ($LASTEXITCODE -ne 0) { throw "csc exited with $LASTEXITCODE" }
} finally { Pop-Location }
