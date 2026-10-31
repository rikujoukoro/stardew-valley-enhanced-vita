# Builds the folder the player copies to ux0:data/StardewValley on the Vita, from their own game
# files: our game build (bin\Release\net40, from `dotnet build Stardew-Valley.csproj -c Release`) and
# the Content folder of their PC game install, with the two XACT wave banks converted to streaming
# banks (Tools\WaveBankTool streamify; in-memory banks would need ~475 MB of RAM).
#
#   .\stage-stardew.ps1                       -> ..\..\..\vita-install\StardewValley
#   .\stage-stardew.ps1 -Target <folder> [-Game <PC game folder>]
#
# Running it again updates the folder; wave banks are only converted again when the source is newer.
# The VPK (build-stardew.sh) carries the runtime, Mono's class libraries and FNA.dll.
param(
	[string]$Target,
	[string]$Game
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path "$PSScriptRoot\..\.."
if (-not $Game) { $Game = Resolve-Path "$repo\..\Stardew Valley Pc Compability" }
if (-not $Target) { $Target = "$repo\..\vita-install\StardewValley" }
$build = "$repo\bin\Release\net40"
$tool = "$repo\Tools\WaveBankTool\bin\Release\net8.0\WaveBankTool.exe"

if (-not (Test-Path "$build\Stardew Valley.exe")) { throw "no game build in ${build}: run dotnet build Stardew-Valley.csproj -c Release" }
if (-not (Test-Path "$Game\Content\XACT\Wave Bank.xwb")) { throw "no game Content in $Game" }
if (-not (Test-Path $tool)) { & dotnet build "$repo\Tools\WaveBankTool\WaveBankTool.csproj" -c Release -v q -nologo }

New-Item -ItemType Directory -Force "$Target\Content\XACT" | Out-Null

# Managed assemblies only: FNA.dll is in the VPK, the native DLLs are Windows builds.
$assemblies = "Stardew Valley.exe", "StardewValley.GameData.dll", "xTile.dll", "BmFont.dll",
	"System.Data.HashFunction.Core.dll", "System.Data.HashFunction.Interfaces.dll", "System.Data.HashFunction.xxHash.dll"
foreach ($a in $assemblies) { Copy-Item "$build\$a" "$Target\" -Force }

# Content without the wave banks; /MIR keeps the copy identical, /XF leaves the converted banks alone.
& robocopy "$Game\Content" "$Target\Content" /MIR /XF "Wave Bank.xwb" "Wave Bank(1.4).xwb" /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
foreach ($bank in "Wave Bank.xwb", "Wave Bank(1.4).xwb") {
	$source = Get-Item "$Game\Content\XACT\$bank"
	$dest = "$Target\Content\XACT\$bank"
	if (-not (Test-Path $dest) -or (Get-Item $dest).LastWriteTime -lt $source.LastWriteTime) {
		& $tool streamify $source.FullName $dest
		if ($LASTEXITCODE -ne 0) { throw "streamify failed for $bank" }
	}
}

$size = (Get-ChildItem $Target -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("staged {0} ({1:N0} MB): copy this folder to ux0:data/ on the Vita" -f (Resolve-Path $Target), ($size / 1MB))
